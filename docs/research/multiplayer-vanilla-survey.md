# Multiplayer — Vanilla Reconnaissance

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/`) + v0 reference (`_reference/`)
**Purpose:** Map vanilla MP architecture so the api kernel exposes the right primitives. Critical because v0's coupler-forces durability mod established initial sync but failed at the client→host change pattern.

> The api kernel is a **usability layer** on top of vanilla's existing MP system, not a replacement. The hard parts (Steam P2P, authorization, snapshot sync, late-join) are already solved by vanilla. We're providing typed mod-friendly primitives over them.

---

## The headline answer (the v0 fix)

Vanilla syncs state through a **dual-layer pattern**:

1. **HostOnly properties** — only the host writes them. Clients receive via PropertyChange broadcast. Identified by key prefix or per-property auth rules on the owning object.
2. **Request messages** — clients send these (e.g., `RequestOilCar`) to ask the host to mutate HostOnly state. Host validates auth, applies, and the resulting PropertyChange broadcasts back. Client's local KVO updates with `SetValueOrigin.Remote` (no re-broadcast loop).

There is no generic typed RPC layer in vanilla. Each request is a custom message struct.

**v0 broke** because it tried to PropertyChange durability directly from clients. Either silently dropped or rejected without the right corrected-value reply path. v0 had no request-message fallback path.

**v1 pattern:** durability is a HostOnly property. Clients send `RequestSetDurability` (or analogous). Host validates and applies. PropertyChange propagates back. Same template vanilla uses for hotbox/oiling/repair.

---

## Steam networking layer

Steamworks P2P. No relay, no complex region logic.

### Components

| Type | File | Role |
|---|---|---|
| `SteamServer` | `Network.Steam/SteamServer.cs` | Host listener; receives raw P2P, deserializes, dispatches |
| `SteamClient` | `Network.Steam/SteamClient.cs` | Client peer; wraps `SteamNetworkingSockets` |
| `Multiplayer` | `Network/Multiplayer.cs` | Session mode, channel selection, IsHost |
| `ClientLobbyHelper` / `ServerLobbyHelper` | `Network.Steam/` | Lobby create/join/metadata |

### Channels

| Channel | Use | Reliability |
|---|---|---|
| Message (1) | PropertyChange, alerts, requests | Reliable |
| Movement (3) | Position updates, sound FX, camera | Unreliable |
| Data (4) | Critical snapshot, AddCars, world state | Reliable |

Selection in `Multiplayer.ChannelForMessage()`. PropertyChange → reliable by default.

Serialization: **MessagePack** binary.

---

## Authority model

Host-authoritative. Static per session (no host migration). Authority is checked at TWO levels.

### Session level

`StateManager.IsHost`, `Multiplayer.IsHost`. Coarse-grained "am I the host."

### Per-property level

```csharp
Car.AuthorizationRequirementForPropertyWrite(key) → AuthorizationRequirement
```

Each property key has an auth requirement, often by prefix:

| Prefix examples | Requirement |
|---|---|
| `_`, `oiled`, `hotbox`, `owned`, `ops.passengerMarker` | HostOnly |
| `load.*`, `ops.waybill`, `ops.repair-dest` | MinimumLevelTrainmaster |
| `door.*`, `gate.*` | MinimumLevelPassenger |
| Default (throttle, reverser, etc.) | MinimumLevelCrew + train-crew check |

### Access levels

`Crew → Trainmaster → Officer → President`. Bit-flag semantics (`Game.AccessControl/AccessLevel`).

Per-object ownership: `Car` implements `IPropertyAccessControlDelegate`. The object decides per-key auth — two keys on the same car can have different rules.

---

## KeyValueObject (KVO) — the property store

KVO is a **local property store with change notification**. Writes are local-first (set value, fire observers), then optionally networked.

| Type | File | Role |
|---|---|---|
| `IKeyValueObject` / `KeyValueStorage` | `KeyValue.Runtime/` | `Get`, `Set(key, value, origin)`, `Observe`, `ObserveKeyChanges` |
| `Value` | `KeyValue.Runtime/Value.cs` | Discriminated union (null, bool, int, float, string, dict, array) |
| `SetValueOrigin` enum | `KeyValue.Runtime/` | `Local` vs `Remote` |

### The origin trick (critical loop preventer)

```csharp
KeyValueStorage.Set(key, value, origin):
  if (origin == Local) {
    OnSetValueLocal?.Invoke(key, value);   // → triggers network broadcast
  }
  NotifyObservers(key, value);             // always fires for UI
```

- `origin = Local` → fires observers AND broadcasts
- `origin = Remote` → fires observers only (no re-broadcast)

Snapshot/late-join sets values with `Remote`. Incoming PropertyChange handlers also use `Remote`. **Our wrapper must enforce this discipline.**

### Important trap (v0 likely fell into this)

KVO **does not enforce network boundaries**. Both clients and host can call `Set()`. Authorization is checked at *message-send time*, not at KVO write time.

So `Set("hotbox", 1, Local)` on a client *will fire local observers as if it worked*. The resulting PropertyChange broadcasts to the host, who rejects it. The client has a transient "wrong" view until the host's correction arrives. Locally it appeared to work. Across the wire it didn't.

---

## PropertyChange — the wire format

```csharp
struct PropertyChange : IGameMessage
{
  string ObjectId;        // car id, consist id, mod object id
  string Key;             // "hotbox", "throttle", "mod.foo.bar"
  IPropertyValue Value;   // typed value union
}
```

### Send path (client)

```
ControlProperties[Throttle] = 0.5
  → Car.SendPropertyChange(Throttle, 0.5)
  → StateManager.ApplyLocal(PropertyChange(...))
  → CheckAuthorizationLocally → ApplyLocally (KVO Set with Local)
  → If Client != null, send PropertyChange via network
```

### Receive path (host)

```
SteamServer.ReceiveMessages → deserialize PropertyChange
  → HostManager.HandleGameMessage(playerId, envelope)
  → CheckAuthorizationForPropertyChange(objectId, key, playerId, accessLevel)
  → If denied: HostRejectMessage → send corrected value back to client
  → If approved: apply to snapshot, broadcast to other clients
```

### Receive path (client)

```
ClientManager.ReceiveMessages → deserialize PropertyChange
  → PropertyObjectManager.HandlePropertyChange
  → KVO.Set(key, value, origin: Remote)   // no re-broadcast
```

---

## Request messages — the client→host pattern

For actions clients can't do directly (mutate HostOnly state), vanilla uses **typed request structs**.

### Examples

| Type | Sender | Purpose |
|---|---|---|
| `RequestOilCar` | Client | Ask host to increment "oiled" on a car |
| `SetRepairMultiplier` | Crew client | Adjust repair rate at industry |
| `SwitchListUpdate` | Train crew | Update consist switch list |
| `Transaction` | Client | Batch of messages applied atomically |

### Pattern

1. Client builds request struct, calls `StateManager.ApplyLocal(request)`
2. Sent to host via Message channel (reliable)
3. Host's `Handle(request)` checks auth via attribute or custom rule
4. If approved, host mutates HostOnly state (e.g., `Car.OffsetOiled(amount)`)
5. State change triggers PropertyChange broadcast
6. All clients (including original sender) receive PropertyChange and update local KVO

### What vanilla doesn't have

- A generic typed RPC system. Each action is a hand-written struct + handler.
- Request/response with reply-data. Requests are fire-and-forget; the response is the resulting PropertyChange.

This is the gap our `IRequestRouter` fills — a typed, mod-friendly request mechanism.

---

## Hotbox/axle case study (the canonical pattern)

### Hotbox

- KVO key: `"hotbox"` (HostOnly via prefix rule)
- Stored on Car (`Car._hotbox` mirrors KVO observer)
- Triggered: host's `Car.OnTrackMovement()` runs probabilistic check based on speed + oil
- `ControlProperties[Hotbox] = 1` (host-side) → KVO Set with Local → PropertyChange broadcast
- Clients receive → KVO Set with Remote → observers fire → UI/effects update

### Client cannot directly trigger hotbox

If a client tries `ControlProperties[Hotbox] = 1`:
1. Local KVO updates (observers fire on the client only)
2. PropertyChange sent to host
3. Host rejects (HostOnly), sends corrected value back
4. Client KVO re-updates from rejection, UI flips back

### How clients influence hotbox indirectly

```csharp
// Client clicks oil point:
StateManager.ApplyLocal(new RequestOilCar(carId, 0.15));
// → host receives, calls Car.OffsetOiled(0.15)
// → KVO["oiled"] increments → PropertyChange broadcast
// → CheckForHotbox uses oiled value → hotbox probability decreases
```

**This is the template for our durability mod's MP shape.**

---

## Late-join (snapshot recovery)

When a client connects, host sends the full snapshot:

```csharp
struct Snapshot {
  Dictionary<string, Snapshot.Car> Cars;
  Dictionary<uint, Snapshot.CarSet> CarSets;
  Dictionary<string, Dictionary<string, IPropertyValue>> Properties;
    // ↑ ALL KVO state for ALL registered objects (including mod-owned)
  Dictionary<string, Snapshot.Player> Players;
  Dictionary<string, Snapshot.TrainCrew> TrainCrews;
  Snapshot.Map map;
}
```

Client populates via `StateManager.PopulateFromRemoteSnapshot`. All KVO sets use `origin: Remote` (no re-broadcast).

**Mod-owned KVO objects are included automatically** if registered via `StateManager.RegisterPropertyObject(objectId, kvo, accessControl)`. Late-joiners get our state for free. We just need to register early enough.

There's no diff-sync or re-snapshot mid-session. If a PropertyChange is dropped, the client stays out of sync until the next change — probably fine for HostOnly properties (host re-sends on any mutation).

---

## Save/load

Host saves; client has no persistence. On load, host's snapshot becomes the truth and is broadcast (same code path as late-join).

Mods get save/load for free if their state is in registered KVO objects.

---

## v0 failure analysis

Couldn't locate the exact v0 durability sync code in `_reference/`, but the v0 api had a `IModPropertySyncService`-shaped surface:

```csharp
// v0 (likely shape)
service.SetFloat(modObjectId, key, value);   // host-only setter
service.OnFloatChanged(modObjectId, handler); // listener
```

This is **host-only writes, all-listeners** — fine for state initiated from host. But there was no path for **client-initiated changes**: no request message system, no host-side handler registration for mod-defined requests. So when client-side computed a new durability value, there was no way to inform the host.

**v1 fix:** the api kernel needs both `IReplicatedState<T>` (KVO wrapper, host-only writes for HostOnly state) AND `IRequestRouter` (typed request layer for client→host mutations). Mods needing client→host changes use both: state is HostOnly; clients send requests; host handles, mutates, broadcasts.

---

## Implications for our api primitives

### `IAuthority` ✅ mostly right

Existing shape stands. Add:
- `AccessLevel CurrentAccessLevel { get; }`
- `bool IsAtLeast(AccessLevel required)`
- `event Action<AccessLevel> AccessLevelChanged`

### `IReplicatedState<T>` — needs reshape

Must be **bound to `(objectId, key, authReq)`** at creation. Not free-floating. Setter routes through KVO Set with auth check; getter reads KVO; observe wraps KVO observers.

```csharp
// shape sketch — not final
interface IReplicatedStateRegistry {
  IReplicatedState<T> GetOrCreate<T>(
    string objectId,
    string key,
    AuthorizationRequirement writeAuth,
    T defaultValue);
}

interface IReplicatedState<T> {
  T Value { get; set; }
  IDisposable Observe(Action<T> handler);
  AuthorizationRequirement WriteAuth { get; }
}
```

### `IRequestRouter` — NEW, fills a real gap

Vanilla has no generic mod-friendly request system. We provide one — typed request structs, host-side handlers, MessagePack envelope, bandwidth-conscious.

```csharp
// shape sketch — not final
interface IRequestRouter {
  void Send<TReq>(TReq request) where TReq : IModRequest;
  IDisposable OnRequest<TReq>(
    Func<TReq, IPlayerContext, RequestOutcome> handler
  ) where TReq : IModRequest;
}

interface IModRequest { /* marker */ }

struct RequestOutcome {
  public bool Accepted;
  public string RejectReason;
}
```

Mods define their own `IModRequest` types. Router serializes via the same MessagePack envelope vanilla uses, dispatches host-side based on type, returns outcome to sender.

### `IModObjectRegistry` — NEW (or fold into state registry)

Mods need to register their KVO objects with `StateManager.RegisterPropertyObject` so snapshot-on-join works. Either fold into `IReplicatedStateRegistry` (registering creates the object) or expose explicitly. Either way, the api owns the registration call so mods don't touch `StateManager` directly.

---

## Open questions

- **Host migration** — vanilla doesn't support it. Out of scope for v1?
- **Late-joiner mod properties** — confirm mods registered before client joins are included in snapshot. (Code suggests yes; worth a runtime test.)
- **Mod message authorization** — `[IMessageAuthorizationRuleAttribute]` is baked into `HostManager`. Do we hook into it, or do mods declare their own auth rules through the request router API?
- **Rejection handling** — when host rejects a client's request, how do mods see it? Need a `RequestRejected` event or callback on `Send`.
- **Bandwidth budgets** — should the request router rate-limit per-mod or per-client? Streams stay in-process by design; cross-wire data is events/requests only — that's a natural cap.

---

## Cross-cutting observations

1. **Layered authorization** — message-level + property-level + access-level. Mods' state goes through all three.
2. **Property/Message duality** — stateful data = property; instantaneous action = message. Our api should make this distinction explicit so mods don't try to use the wrong tool.
3. **Origin tracking is the loop preventer** — `SetValueOrigin.Local/Remote`. Our wrapper has to enforce it.
4. **Snapshot is the consistency mechanism** — nothing else handles drift mid-session.
5. **The existing system works** — vanilla's pattern is proven (hotbox, oiling, repair). Our api is a usability layer.
6. **Host-only writes + client requests is THE pattern**. Don't try to invent something different. v0's mistake was trying to make client writes "just work" without the request indirection.

---

## File path index (for implementation reference)

Authority + auth:
- `Game.State/StateManager.cs` (lines 76-110, 400+)
- `Game.AccessControl/PropertyChangeAuthorizationRuleAttribute.cs`
- `Game/HostManager.cs` (lines 782-820)

KVO + PropertyChange:
- `KeyValue.Runtime/KeyValueStorage.cs`
- `Game.Messages/PropertyChange.cs`
- `Model/Car.cs` — authorization + hotbox
- `RollingStock/OilPointPickable.cs` — request pattern example

Message routing:
- `Network.Client/ClientManager.cs` (lines 137-151, 212-219)
- `Network.Steam/SteamServer.cs` — message receive

Snapshot:
- `Game.Messages/Snapshot.cs`
- `Game.State/StateManager.cs` — `PopulateFromRemoteSnapshot`
