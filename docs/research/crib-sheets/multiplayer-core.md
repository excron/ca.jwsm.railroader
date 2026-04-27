# Multiplayer Core — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`, `Railroader-ILSPY/KeyValue.Runtime/`)
**Companions:** [KVO Patterns](kvo-patterns.md), [State Manager](state-manager.md), [Request Messages](request-messages.md)
**Narrative survey:** [`../multiplayer-vanilla-survey.md`](../multiplayer-vanilla-survey.md)

Railroader's multiplayer is host-authoritative Steamworks P2P. The host is the "server" but is not headless — it's a player too, with implicit `AccessLevel.President`. There is no host migration, no relay, no listen-or-dedicated split. Singleplayer goes through the *same code path* as MP-host using a `LocalGameClient` that short-circuits the wire. Every system in the game flows through three primitives: `IGameMessage` (typed RPC-ish messages), `KeyValueObject` (per-object property store with KVO observers), and `Snapshot` (the full late-join handoff). MessagePack is the wire codec; gzip wraps any payload over 1 KiB.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Multiplayer` (static) | `Network/Multiplayer.cs:20` | Session state. Holds `Host`, `Client`, `Mode`. Channel router |
| `Multiplayer.IsHost` | `Network/Multiplayer.cs:43` | True if `Host != null` (singleplayer = host) |
| `HostManager` | `Game/HostManager.cs:24` | Server-side message router + auth + snapshot recording |
| `ClientManager` | `Network.Client/ClientManager.cs:20` | Client-side dispatcher; wraps a `GameClient` |
| `GameClient` (abstract) | `Network.Client/GameClient.cs:13` | Base for `SteamClient` / `LocalGameClient` |
| `SteamServer` / `SteamClient` | `Network.Steam/` | Steamworks `SteamNetworkingSockets` P2P transport |
| `LocalGameClient` | `Network.Client/LocalGameClient.cs:11` | In-process loopback for SP/host. **Same `HostManager.HandleMessage` path** |
| `Channel` enum | `Network/Channel.cs:3` | `Message=1` reliable, `Movement=3` unreliable, `Data=4` reliable |
| `Multiplayer.ChannelForMessage` | `Network/Multiplayer.cs:205` | Maps `IGameMessage` → channel. Defaults to `Message` (reliable) |
| `Common.CurrentVersion` | `Network/Common.cs:14` | Protocol `0.3` (Major/Minor pair); checked in `Hello` handshake |
| `MessagepackSupport.Setup()` | `Game.Messages/MessagepackSupport.cs:12` | Idempotent MP resolver registration. Called from `GameClient.Setup` |

---

## Session topology

```
Singleplayer:         StartSingleplayerSetup    → HostManager + LocalGameClient (loopback)
Multiplayer host:     StartMultiplayerHostSetup → HostManager + SteamServer + LocalGameClient
Multiplayer client:   JoinMultiplayerSetup      → SteamClient → remote SteamServer
```

`Multiplayer.PrepareHostIfNeeded` (`Multiplayer.cs:59`) creates `Host = new HostManager()` only for SP and MP-host. `Multiplayer.ConnectClient` (`Multiplayer.cs:76`) always creates a `ClientManager` regardless of mode — the host *is also a client* connected to itself via `LocalGameClient`.

**Implication:** `StateManager.Handle(message, sender)` runs even on the host's own UI clicks. Host code that calls `ApplyLocal` round-trips through its own dispatcher, which is why `HandleSetMultiplier`, `HandleRequestOilCar`, etc. *don't* gate on `IsHost` at the entry — the auth wrapper does.

### `Multiplayer.IsHost` (`Network/Multiplayer.cs:43`)

```csharp
public static bool IsHost
{
    get
    {
        if (Host == null) return !Application.isPlaying;   // editor: treat as host
        return true;
    }
}
```

**Editor-mode quirk:** `IsHost` returns `true` outside Play mode even with no `HostManager` instance. Patches that read `IsHost` during edit-time reflection see the host branch.

### `Multiplayer.Mode` enum (`Network/ConnectionMode.cs`)

`Singleplayer | MultiplayerClient | MultiplayerServer`. Set in `ConnectClient` based on `INetworkSetup` subtype.

`StateManager.IsSandbox` (`StateManager.cs:124`) is independent of Mode — sandbox is per-save (`GameMode == GameMode.Sandbox`), not per-session.

---

## Steamworks P2P transport

### `SteamServer` (`Network.Steam/SteamServer.cs`)

- `OnEnable` (l.45): registers `SteamNetConnectionStatusChangedCallback_t`.
- `StartListening` (l.74): `SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null)`.
- `Update` (l.69): `ReceiveMessages()` polls `_pollGroup` up to 32 messages per tick (`_receivedMessagePointers.Length`), recurses if buffer full.
- Auth: `OnConnectionStatusChanged` (l.109) → `Delegate.ShouldAcceptConnection(steamID)` → `AcceptConnection`. `HostManager.ShouldAcceptConnection` (l.220) **always returns `true`** — Steam ID-based bans happen at the application layer (`PlayerRecord.AccessLevel == Banned`), not at the connection layer.

### `SteamClient` (`Network.Steam/SteamClient.cs`)

- `Connect` (l.68): `SteamNetworkingSockets.ConnectP2P(remote, 0, 0, null)`.
- `SendNetworkMessage` (l.91): one-shot to `_connection`. **On send error, `Movement` channel sends are dropped silently; everything else triggers `Disconnect()`** (l.95-104). This matters for unreliable position spam during transient connection issues.

### `RawNetworkMessage` (`Network.Steam/RawNetworkMessage.cs`)

Marshalled view of `SteamNetworkingMessage_t`. Includes `Channel` field but the channel is *informational only* — Steam already routes by send-flags, not by per-message channel demux.

### `SendContext.Send` (`Network.Steam/SendContext.cs:46`) — the wire-level pipeline

```csharp
1. Channel = Multiplayer.ChannelForMessage(networkMessage)   // routes by message type
2. MessagePackSerializer.Serialize(_bufferWriter, networkMessage)
3. if (_bufferWriter.ArrayLength > 1024) {                    // GZIP THRESHOLD
       gzip into _gzipMemoryStream
       wrap in NetworkMessageEnvelope(flags0=1, flags1=0, gzipBytes)
       re-serialize the envelope
   }
4. SendToRecipients: SteamNetworkingSockets.SendMessageToConnection(conn, ptr, len, sendFlags, _)
       sendFlags: Message=8 (Reliable), Movement=0 (Unreliable), Data=8 (Reliable)
```

**Compression is per-message, fixed threshold 1024 bytes.** Snapshots, `AddCars`, `Transaction`, and `SwitchListUpdate` will almost always be gzipped; per-tick `BatchCarPositionUpdate` typically is not.

`ReceiveContext.NetworkMessageFromPointer` (`ReceiveContext.cs:17`) detects the envelope by union-tag at deserialize, then decompresses if `Flags0=1, Flags1=0`. Other flag combos throw — the envelope is currently 1-bit usable.

### Buffer sizes

`_gzipDestStream = new WriteBufferStream(new byte[131072])` (`ReceiveContext.cs:15`) — 128 KiB max decompressed payload. **Hard ceiling on per-message size after decompression**; oversized messages will throw on receive.

---

## Channel routing (`Multiplayer.ChannelForMessage`, `Network/Multiplayer.cs:205`)

| Message | Channel | Notes |
|---|---|---|
| `AddCars`, `SwitchListUpdate`, `Transaction` | `Data` (reliable) | Bulk / state-critical |
| `UpdateCharacterPosition`, `UpdateCameraPosition` | `Movement` (unreliable) | Per-tick avatar/camera spam |
| `BatchCarPositionUpdate` (Critical=false) | `Movement` (unreliable) | Per-tick train positions |
| `BatchCarPositionUpdate` (Critical=true) | `Message` (reliable) | At-rest snapshot |
| `TurntableUpdateAngle` | `Movement` (unreliable) | While rotating |
| `PlaySoundAtPosition` | `Movement` (unreliable) | Effects |
| Everything else (incl. `PropertyChange`) | `Message` (reliable) | Default |
| `forceReliable=true` override | `Message` | Used by `Transaction` flush, snapshot kickers |

Snapshot-level `INetworkMessage` (the `SnapshotEnvelope`) → `Data` channel. Anything else (`Hello`, `Login`, `ClientStatus`, `TimeSync`, `Alert`, ...) → `Message`.

**Implication:** PropertyChange is reliable. Drop-resilience for state changes is *not* an issue at this layer; it's an issue at the rejection-handling layer (see [State Manager § rejection](state-manager.md#rejection-and-corrections)).

---

## Connection lifecycle (`Network.Server.ClientStatus`)

```
Initial → Hello → Anonymous → Login → Authenticated → RequestActive → Active
                                  ↘ PasswordRequired (PasswordPrompt back to client)
                                  ↘ AccessDenied/VersionMismatch (disconnect)
```

| Stage | Allowed messages | Source |
|---|---|---|
| `Initial` | `Hello`, `Goodbye` only | `HostManager.HandleMessageInitial` (l.271) |
| `Anonymous` | `Login`, `Goodbye` only | `HandleMessageAnonymous` (l.296) |
| `Authenticated` | `RequestActive`, `Goodbye` | `HandleMessageAuthenticated` (l.368) |
| `Active` | `GameMessageEnvelope`, `TimeSync`, `Goodbye` | `HandleMessageActive` (l.389) |

`SetAndSendClientStatus` (l.414) advances the FSM and pushes `Network.Messages.ClientStatus(status, playerId, accessLevel)` to the client. `GameClient.HandleMessage` (`GameClient.cs:53`) flips `ServerClientStatus` and triggers `SendLogin` on transition to `Anonymous`.

### Active gate

```csharp
public void Send(IGameMessage message, Channel channel)   // GameClient.cs:41
{
    if (ServerClientStatus != ClientStatus.Active) {
        Log.Warning("Will not send ...");                 // SILENT DROP
        return;
    }
    SendNetworkMessage(new GameMessageEnvelope(PlayerId.String, message), channel);
}
```

**Pre-Active sends are silently dropped** with only a log warning. Mods that send during early init must wait for `ClientStatus.Active` (subscribe via `IClientDelegate.ClientStatusDidChange` or wait for `PropertiesDidRestore` Messenger event — see [State Manager § lifecycle](state-manager.md#lifecycle--phases)).

### Pending Active (snapshot gating)

`HostManager.HandlePendingRequestActive` (l.351) holds `RequestActive` until `_hasLoadedSnapshot` is true. The host won't promote any client to Active until its own save is loaded. Late-joiners during host-side load are queued — `_pendingRequestActive` set, drained in `LoadSnapshot` (l.741).

### Disconnect reasons (`Network/DisconnectReason.cs`)

```
Goodbye=1001, NoMorePassengers=1002,
AccessDenied=2001, VersionMismatch=2002, PasswordRequired=2003,
HostClosedConnection=2999, Timeout=5003, PeerSentNoConnection=5010
```

`ClientManager.ClientDidDisconnect` (`ClientManager.cs:153`) maps codes to user-facing strings.

---

## Lobby (`Network.Steam/`)

| Type | Role |
|---|---|
| `ServerLobbyHelper` | Host: create lobby, set `ver`/`status`/`rpmk` metadata |
| `ClientLobbyHelper` | Client: list / filter lobbies, join, fetch GameServerId |
| `LobbyType` | enum (`Public/FriendsOnly/Invisible`?) |
| `LobbyKeys` | constants for lobby metadata strings |

`ClientLobbyHelper.FetchLobbies` filters by app version, `status=open`, and optional reporting mark (`Network.Steam/ClientLobbyHelper.cs:38`). After joining, awaits `LobbyGameCreated_t` callback then reads `GameServer.id` for the actual P2P target.

`Multiplayer.UpdateLobbyFlags` (`Multiplayer.cs:135`) republishes `AllowNewPlayers`, `HasNewPlayerPassword`, `RailroadMark` after settings change. **Called from `StateManager.PopulateFromRemoteSnapshot`** — late-joiners trigger a lobby metadata refresh on the host. Don't depend on lobby-metadata stability mid-session.

---

## Authentication (`HostManager.Authenticate`, `HostManager.cs:535`)

```
host's own SteamID? → AccessLevel.President (always)
known PlayerRecord? → record.AccessLevel
AllowNewPlayers off? → AccessDenied
NewPlayerPasswordHash empty? → DefaultAccessLevel (no password)
password matches? → DefaultAccessLevel
empty password? → PasswordRequired (sends PasswordPrompt)
wrong password? → PasswordRequired
```

Then `AccessLevel.Banned` triggers immediate disconnect (l.325). `AccessLevel.Passenger` checks `NumPassengersOnline >= storage.PassengerLimit` and disconnects with `NoMorePassengers` if full.

`ValidateUsername` (`HostManager.cs:596`) currently **always returns true** even on duplicate-name conflict; it logs but does not reject. The "would fail" log strings in source are vestigial.

Username sanitization: `StringSanitizer.SanitizeName` strips bad chars; null/whitespace passes through (logged as "would fail" but accepted).

---

## Message envelopes (`Network.Messages/`)

```csharp
[Union(0..13)]                                   // INetworkMessage union
public interface INetworkMessage { }             // Network/INetworkMessage.cs
```

| Union tag | Type | Purpose |
|---|---|---|
| 0 | `Hello` | Version handshake |
| 1 | `Goodbye` | Disconnect intent |
| 2 | `Login` | Auth (username, password, customization) |
| 3 | `ClientStatus` | Server → client FSM update |
| 4 | `RequestActive` | Client → host: please promote me |
| 5 | `PlayerList` | All connected players (broadcast on connect/disconnect) |
| 6 | `TimeSync` | NTP-style; `TimeSynchronizer` consumer |
| 7 | `PasswordPrompt` | Server → client: password required |
| 8 | `NetworkMessageEnvelope` | Compressed inner-message wrapper |
| 10 | `GameMessageEnvelope` | (sender, IGameMessage) — the bulk of traffic |
| 11 | `SnapshotEnvelope` | Full-state initial sync |
| 12 | `Alert` | Toast/console notification |
| 13 | `SetPlayerPosition` | Initial-spawn position |

`GameMessageEnvelope` is `(sender: string, gameMessage: IGameMessage)` — the sender field is set by `HostManager.HandleGameMessage` (l.703) so clients can't spoof. **Original sender passed in by `GameClient.Send` is overwritten host-side before re-broadcast.**

### `IGameMessage` union (`Game.Messages/IGameMessage.cs`)

70+ types, union tags 10..431. Adding a new mod-side message type requires a `[Union(N, typeof(...))]` registration — vanilla doesn't expose a hook for this, so mods must inject via reflection, Harmony patch on `MessagepackSupport.Setup`, or piggyback on `PropertyChange` with mod-prefixed keys.

See [Request Messages](request-messages.md) for the full per-type catalog.

---

## Player ID, time sync

`PlayerId` (`Game/PlayerId.cs`) wraps a `ulong` SteamID; static `Invalid` and `IsValid`. The host's PlayerId is `MySteamId`. In-process clients share the host's PlayerId — `LocalGameClient` reports `Multiplayer.MySteamId` on connect.

`PlayersManager.PlayerId` (static, `PlayersManager.cs`) is the *current local-player* id — the LocalPlayer for the host, the connected SteamID for clients. Used in auth checks (`StateManager.CheckAuthorizedToSendMessage`).

`TimeSynchronizer` (`Network.Client/TimeSynchronizer.cs`) — NTP-style RTT measurement. `Tick` is server-clock-aligned milliseconds since session start. Used by `BatchCarPositionUpdate.Tick`, `TurntableUpdateAngle.Tick`, `UpdateCharacterPosition.Tick` for client-side reconciliation.

`StateManager.Now` (`StateManager.cs:80`):
```csharp
return Multiplayer.Client != null ? Multiplayer.Client.Tick : NetworkTime.systemTick;
```

---

## Patch candidates

| Method | Why patch |
|---|---|
| `Multiplayer.ChannelForMessage(IGameMessage)` | Reroute custom messages to a different channel (bandwidth tuning). Only safe if mods register for a `[Union]` tag. |
| `SendContext.Send` | Intercept all outbound serialization for telemetry / size accounting. Hot path — keep it cheap. |
| `ReceiveContext.NetworkMessageFromPointer` | Inspect inbound. Errors here disconnect — don't throw. |
| `HostManager.HandleGameMessage` | Wrap host-side dispatch. Useful for global rate-limits per playerId. |
| `HostManager.RoutingForMessage` | Custom relay rules (e.g., broadcast to mod-specific subset). Currently auth-then-AllExcept; SwitchListUpdate is the only special case. |
| `HostManager.CheckAuthorizedToSendMessage` (static) | Override message-level auth across the board. Runs both client-side (in `StateManager.ApplyLocal`) and host-side. |
| `HostManager.Authenticate` | Custom auth (e.g., external whitelist). Currently password + record-based. |
| `GameClient.HandleMessage` | Client-side fan-out point. Patch to add custom INetworkMessage union members. |
| `MessagepackSupport.Setup` | Register additional resolvers. Idempotent; only first call wins (`_hasSetupMessagepack` static bool). |

---

## MP authority summary

| Layer | Where checked | What it gates |
|---|---|---|
| Connection | `HostManager.ShouldAcceptConnection` (always true) + `Authenticate` | Who can join the session at all |
| Message | `[MinimumAccessLevel]` / `[HostOnly...]` / `[PropertyChangeAuthorizationRule]` attribute on the IGameMessage struct | What types of action this player can request |
| Property | `IPropertyAccessControlDelegate.AuthorizationRequirementForPropertyWrite(key)` per object | What KVO keys this player can write |
| Train-crew | `MinimumLevelCrew` requirement extends to a `trainCrewId` membership check (`StateManager.SenderSatisfiesAuthorizationRequirement`, l.1393) | Crew-only operations on cars assigned to a train crew the player is not on |

See [State Manager](state-manager.md) for the auth resolver, [KVO Patterns § HostOnly](kvo-patterns.md#hostonly-and-the-prefix-system) for the `_`-prefix rule, and [Request Messages](request-messages.md) for the per-message catalog.

---

## Gotchas

- **`MessagepackSupport.Setup` runs from `GameClient.Setup`** — i.e. only when a client is created (SP, MP-host, MP-client). If a mod tries to serialize an `IGameMessage` before `Connect()`, the resolver chain is bare-Unity defaults. Force-call `MessagepackSupport.Setup()` in the mod's init if you need pre-connect serialization.
- **Singleplayer is multiplayer-with-one-client.** All the auth, PropertyChange round-tripping, and snapshot machinery runs in SP. There is no SP-only fast path. This means: SP can have rejection bugs that would never trigger if the host code "just wrote the value" — but it doesn't, it always goes through `ApplyLocal`.
- **`LocalGameClient` defers send/receive by one frame** (`Update` drains `_pendingSend`/`_pendingReceive`). Even SP messages don't apply synchronously to the host — there's a one-frame round-trip. Patches that expect synchronous KVO updates after `ApplyLocal` will see stale state.
- **The 1024-byte gzip threshold is a constant**, not configurable. Per-message overhead from the envelope (`Flags0`, `Flags1`, `ArraySegment` MessagePack header) adds ~10 bytes; at borderline sizes a payload may *grow* after gzip. Acceptable but worth knowing.
- **Compressed envelopes are not nested-decompressible.** `ReceiveContext` only unwraps once. Sending `NetworkMessageEnvelope` inside `NetworkMessageEnvelope` will throw on the inner deserialize because the union expects a real INetworkMessage type next.
- **`SteamServer.SendTo(ClientId, INetworkMessage)` is empty** (`SteamServer.cs:225`). Direct sends route via the host's `SendContext` chain (`HostManager.SendToClient` → `SendToClients` → `_sendContext.Send`). The `IServerManager.SendTo` method exists for the `LocalGameClient` interface contract only.
- **`HostManager.ShouldAcceptConnection` always returns true.** Banned-player handling happens after Login. A spammy reconnect loop from a banned Steam ID will burn auth-handshake CPU on the host.
- **`StateManager.IsHost` is `Multiplayer.IsHost`**, which is `true` outside Play mode. This trips up Editor inspector code that reads `IsHost` during recompile / domain reload.
- **The `Channel` enum values are `1, 3, 4`** — not contiguous. There's no Channel 0 or Channel 2; they're reserved for Steam internals. Don't serialize as `(byte)channel` assuming compactness.
- **Movement-channel send errors are silently dropped** (`SteamClient.SendNetworkMessage` l.95). For unreliable spam this is correct; but it also means transient network errors during avatar updates won't surface. Patches that care about per-message success need to ignore Movement-channel errors specifically.
- **Username validation does not reject duplicates** — `ValidateUsername` always returns true after logging the conflict (`HostManager.cs:608`). Two players with the same name will both connect.
- **`LocalGameClient.Setup` requires `HostManager.Shared` to be non-null** (`LocalGameClient.cs:30`). `Multiplayer.PrepareHostIfNeeded` must run *before* `ConnectClient`. The current entry points (`StartSingleplayerSetup`, `StartMultiplayerHostSetup`) handle this correctly; mods triggering reconnect must replay both.
- **`Application.isPlaying` is checked in `Multiplayer.IsHost`** — Editor auto-tests with `Application.isPlaying=false` see `IsHost=true` *and* `Multiplayer.Client=null`, which is the only state where `StateManager.AccessLevel` returns `President` without a session. Useful for build-time codepath testing.

---

## Init order pitfalls

1. **`MessagepackSupport.Setup` must run before any deserialize.** `GameClient.Setup` calls it; if a mod deserializes mod state before connect, the static call is required.
2. **`HostManager` exists before `ClientManager`.** `Multiplayer.PrepareHostIfNeeded` runs synchronously; `ConnectClient` is async. SP/MP-host can transiently observe `Host != null && Client == null`.
3. **`StateManager.Shared` is set in `OnEnable` (`StateManager.cs:216`).** It's a MonoBehaviour. Patches running before scene load see `Shared == null` and `ApplyLocal` no-ops with a Debug.LogWarning.
4. **Snapshot population happens *after* StateManager.Shared and TrainController.Shared exist** but *before* `PropertiesDidRestore` fires. Mod-registered KVO objects must be `RegisterPropertyObject`'d before `PopulateFromRemoteSnapshot` runs, or their snapshot data goes into a synthetic `KeyValueStorage` with `HostOnly` static auth and a deferred restore origin (`PropertyObjectManager.RestoreProperties` l.86).
5. **`OnPropertiesDidRestore` is the official "ready" hook.** All sub-managers (`Loan`, `_observers`, `Tutorial`, `_loanManager`) wire up here. Mods should subscribe `Messenger.Default.Register<PropertiesDidRestore>(...)` rather than `Awake`/`Start`.
6. **`UpdateLobbyFlags` runs at end of `PopulateFromRemoteSnapshot` (`StateManager.cs:1201`).** Lobby metadata flips after every snapshot population — late join, save load, host kick-and-readd. Mods reading lobby metadata for handshake purposes should re-read on `PropertiesDidRestore`.

---

## Cross-references

- [KVO Patterns](kvo-patterns.md) — `KeyValueObject` write semantics, the `OnSetValueLocal` broadcast hook, observer disposal.
- [State Manager](state-manager.md) — `ApplyLocal` flow, `Handle` dispatcher, snapshot semantics, auth resolver, transactions.
- [Request Messages](request-messages.md) — full catalog of every `IGameMessage` and where each is handled.
- [Wear & Durability § MP authority](wear-durability.md#mp-authority) — example of HostOnly+request-message pattern in practice.
- [Couplers § Wire format & MP authority summary](couplers.md#wire-format--mp-authority-summary) — example of mixed HostOnly + per-key auth on a single object (`Car`).
