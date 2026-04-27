# Players & Train Crews — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Multiplayer Core](multiplayer-core.md), [State Manager](state-manager.md), [Player & Camera](player-camera.md), [Request Messages](request-messages.md)

`PlayersManager` is the in-session roster. It owns the `LocalPlayer` struct, a dictionary of `RemotePlayer` MonoBehaviours, the `_trainCrews` dictionary, and a per-player `(camera position, time)` cache used for proximity gating. It is **not** the source of truth for AccessLevel — that lives on `HostManager._playerRecords` (host) and `Multiplayer.Client.AccessLevel` (client). `_trainCrews` is host-authoritative state replicated as a *whole-dictionary* `UpdateTrainCrews` snapshot every time anything changes; there is no per-crew incremental wire message. Train crews are pure metadata containers (`Id, Name, Description, MemberPlayerIds, TimetableSymbol`) — they own no cars, schedules, or money. The Crew↔car relationship is one-way: each `Car.trainCrewId` points at a crew, the crew has no back-reference. The `Crew` access level is the *floor*; train-crew membership is a *separate* gate that only kicks in when `_storage.TrainCrewMembershipRequired` is true.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `PlayersManager` | `Game.State/PlayersManager.cs:23` | Roster + train-crew owner; lives on `StateManager._playersManager` |
| `PlayersManager.PlayerId` (static) | `Game.State/PlayersManager.cs:58` | Local player's `PlayerId` (cached `User.Client.Id`) |
| `PlayersManager.LocalPlayer` | `Game.State/PlayersManager.cs:88` | The single `LocalPlayer` struct instance |
| `PlayersManager.RemotePlayers` | `Game.State/PlayersManager.cs:54` | Live `RemotePlayer` MonoBehaviours (excludes self) |
| `PlayersManager.AllPlayers` | `Game.State/PlayersManager.cs:72` | LocalPlayer + RemotePlayers; **iterator** |
| `PlayersManager.MyTrainCrew` | `Game.State/PlayersManager.cs:91` | The `TrainCrew` containing local PlayerId, or null |
| `PlayersManager.TrainCrews` | `Game.State/PlayersManager.cs:56` | Name-sorted readonly list |
| `TrainCrew` | `Game.State/TrainCrew.cs:7` | Plain class: `Id`, `Name`, `Description`, `MemberPlayerIds`, `TimetableSymbol` |
| `IPlayer` / `LocalPlayer` / `RemotePlayer` | `Game/IPlayer.cs`, `Game/LocalPlayer.cs`, `Network.Client/RemotePlayer.cs` | Player handles. LocalPlayer is a **struct** |
| `PlayerId` | `Game/PlayerId.cs` | Wraps SteamID as `string` |
| `Snapshot.Player` / `Snapshot.TrainCrew` | `Game.Messages/Snapshot.cs:31, 132` | Wire format for save/late-join |
| `HostManager._playerRecords` | `Game/HostManager.cs:147` | Persistent per-Steam-ID record (`PlayerRecord`) — auth source of truth |
| `PlayerRecordsClientManager` | `Game.State/PlayerRecordsClientManager.cs` | Trainmaster+ snapshot of `_playerRecords` |
| `GameStorage.TrainCrewMembershipRequired` | `Game.State/GameStorage.cs:213` | "Restrict Equipment Control to Train Crew" toggle |
| `GameStorage.TrainCrewMembershipManagedByTrainmaster` | `Game.State/GameStorage.cs:225` | "Trainmaster Manages Crew Assignments" toggle |

---

## Player vs. avatar vs. record — three concentric layers

Railroader has **three** notions of "player" that are easy to confuse. They have different lifetimes and different sources of truth:

```
                     LIFETIME              SOURCE OF TRUTH         AUTH-RELEVANT?
PlayerRecord         persistent (file)     HostManager._playerRecords  YES (AccessLevel)
Snapshot.Player      online session        HostManager._snapshot.players  YES (live AccessLevel)
RemotePlayer         online + visible      PlayersManager._remotePlayers  NO (visual only)
```

**`PlayerRecord`** (`Game.Persistence/PlayerRecord.cs`) — saved to the host's save file. Holds `Name`, `SteamId`, `AccessLevel`, `AccessLevelChanged`, `LastConnected`, last-known `Position`. The host upserts a record on every successful login (`HostManager.PostActivate`, l.621). **Bans persist via a record with `AccessLevel.Banned`.** Removing a record = forgetting the player; the next login treats them as new. `RemovePlayerRecord` is President-only and refuses online players.

**`Snapshot.Player`** — the in-snapshot "currently connected" entry. Per-Steam-ID, includes `Name`, `AccessLevel`, `Customization`, `Position`. Cleared from `_snapshot.players` on disconnect. Wire-format for the late-join snapshot and the periodic `PlayerList` broadcast. Note: `PopulateSnapshotForSave` (`PlayersManager.cs:455`) **explicitly nulls `snapshot.players`** before serialization — players are not saved as part of the world snapshot, only `PlayerRecord`s persist across sessions.

**`RemotePlayer`** — a `MonoBehaviour` on a child of `PlayersManager.transform`, created lazily for non-self entries in the snapshot. Holds the `RemoteAvatar` ref. Pure visual; no network state. See [Player & Camera › `RemoteAvatar`](player-camera.md#avatarremoteavatar-other-players-bodies-mp).

**The local player has no `RemotePlayer` instance.** `PlayersManager._remotePlayers` is keyed by remote `PlayerId`; local PlayerId is filtered out in `SplitSnapshotPlayers` (`PlayersManager.cs:229`).

---

## `Game.PlayerId` (the SteamID wrapper)

```csharp
public readonly struct PlayerId {                     // Game/PlayerId.cs
    private readonly string _playerId;                // ulong SteamID stringified ("D" format)
    public static readonly PlayerId Invalid;          // _playerId == null
    public string String   => _playerId;
    public bool   IsValid  => !string.IsNullOrEmpty(_playerId);
    public PlayerId(string playerId);                 // for snapshot deserialize
    public PlayerId(ulong steamId);                   // → ToString("D")
    // Equals / == / != by string compare; HashCode by string
}
```

**Stringly-typed.** All wire formats and dictionary keys use the `string` form (`Snapshot.Player` keyed by `string`, `MemberPlayerIds` is `HashSet<string>`). The `PlayerId` struct is the in-memory wrapper. Convert via `new PlayerId(string)` and `playerId.String`.

`PlayersManager.PlayerId` (static, l.58) lazy-caches `new PlayerId(User.Client.Id)` on first access — that's the local Steamworks user. Cleared **never** (no logout flow); persists across save loads.

### Patch candidates

| Method | Why patch |
|---|---|
| `PlayerId` cctor / static cache | Invalidate the cached id if you need to "switch user" mid-process (vanilla never does). Set `_cachedPlayerId = null` via reflection. |

### Gotchas

- **`PlayerId.Invalid` is `default(PlayerId)`**, which has `_playerId = null`. `IsValid` returns false; `Equals(default)` is true. All snapshot deserializes that fail produce `Invalid`. Don't use `Invalid` as a dictionary key — it'll collide with any other invalid-id source.
- **Steam offline mode**: `User.Client.Id` returns a placeholder when Steamworks isn't initialized. `PlayersManager.PlayerId` will return that placeholder; auth checks compare against `HostPlayerId` which is `MySteamId` from the same source — so SP works fine, but multi-account rapid-switch on the same machine will reuse the placeholder.

---

## `Game.IPlayer` / `LocalPlayer` / `RemotePlayer`

```csharp
public interface IPlayer {                            // Game/IPlayer.cs
    string    Name         { get; }
    bool      IsRemote     { get; }
    PlayerId  PlayerId     { get; }
    Vector3   GamePosition { get; }                   // game space (post-floating-origin)
    RemotePlayer CheckedRemotePlayer();               // throws if local
}

[StructLayout(LayoutKind.Sequential, Size = 1)]       // empty struct, 1-byte sentinel
public struct LocalPlayer : IPlayer {                 // Game/LocalPlayer.cs
    public string   Name        => Preferences.MultiplayerClientUsername;
    public bool     IsRemote    => false;
    public PlayerId PlayerId    => PlayersManager.PlayerId;
    public Vector3  GamePosition => CameraSelector.shared.localAvatar.character.GroundPosition.WorldToGame();
    public RemotePlayer CheckedRemotePlayer() => throw new Exception("LocalPlayer instance is not RemotePlayer");
}
```

**`LocalPlayer` is a stateless struct.** Every property is a delegated read from a static. `PlayersManager._localPlayer` is `default(LocalPlayer)` — the only instance the manager hands out. **All "local player" data lives elsewhere** (Preferences, CameraSelector, PlayersManager.PlayerId). Not a `MonoBehaviour`; you cannot AddComponent to it. See [Player & Camera § Game.IPlayer / LocalPlayer / RemotePlayer](player-camera.md#gameiplayer--gamelocalplayer--networkclientremoteplayer) for the storage-attachment patterns.

**Naming clash:** there is also a `Game.LocalPlayer` *struct* (this) and a `Character.LocalAvatar` *MonoBehaviour* (the visible avatar for the local player). Different things.

```csharp
public class RemotePlayer : MonoBehaviour, IPlayer {  // Network.Client/RemotePlayer.cs
    public PlayerId    playerId;
    public string      playerName;
    private RemoteAvatar _avatar;
    public string   Name         => playerName;
    public bool     IsRemote     => true;
    public PlayerId PlayerId     => playerId;
    public Vector3  GamePosition => _avatar.transform.position.WorldToGame();
    public RemoteAvatar AddUpdateAvatar(AvatarDescriptor);
    public void  UpdateAvatarPosition(Vector3, Vector3, Vector3, Vector3, string, AvatarPose, long);
    public void  ConfigureAvatar(Vector3, string, Vector3, Vector3, Snapshot.CharacterCustomization);
    public RemotePlayer CheckedRemotePlayer();         // returns this
}
```

`OnDestroy` calls `AvatarManager.Instance.RemoveAvatar(_avatar)` — destroying the `RemotePlayer` GameObject is the canonical way to despawn a player visually.

### Patch candidates

| Method | Why patch |
|---|---|
| `LocalPlayer.GamePosition` getter | Override "where the local player is" — e.g., for cameras vs. body. Property is on a struct; patching requires Harmony struct-method support. |
| `RemotePlayer.AddUpdateAvatar` | Inject mod-side avatar attachments at first appearance. |
| `RemotePlayer.UpdateAvatarPosition` | Pre-`AddPosition` interception (rate, validation, custom interp). |
| `RemotePlayer.OnDestroy` | Run cleanup before the avatar is removed. |

### Gotchas

- **`LocalPlayer` `Name` reads `Preferences.MultiplayerClientUsername`**, which is a PlayerPrefs string editable in Settings. `StateManager.OnEnable` (`StateManager.cs:208-210`) seeds it from `User.Client.Id.Name` if empty. Renaming yourself mid-session does NOT broadcast — the host already has your name from the `Login` message. `ClientManager.SendCharacter()` is what re-broadcasts the name + customization (called on Active and on lantern toggle); `Preferences.MultiplayerClientUsername` is read at that point.
- **`RemotePlayer.GamePosition` will NPE if `_avatar` is null.** It's null between `CreateRemotePlayer` and `ConfigureAvatar`. Code iterating `AllPlayers` immediately after a snapshot ingest may hit this race.
- **`AllPlayers` yields `LocalPlayer` first, then RemotePlayers** — and *only* yields RemotePlayers if `Multiplayer.Client != null` (`PlayersManager.cs:77`). Pre-connect, you only see yourself.

---

## `PlayersManager` — roster + train-crew home

### State

```csharp
private readonly Dictionary<PlayerId, RemotePlayer>    _remotePlayers   = new();   // 38
private readonly Dictionary<PlayerId, PlayerCameraPosition> _lastKnownPositions = new(); // 40
private          Dictionary<string, TrainCrew>         _trainCrews      = new();   // 42
private          List<TrainCrew>                       _orderedTrainCrews;          // 44 (Name-sorted cache)
private readonly LocalPlayer                           _localPlayer;                // 46 (default struct)
private          bool                                  _hasNotifiedOfPlayers;       // 48
private static   PlayerId?                             _cachedPlayerId;             // 50
private readonly Dictionary<PlayerId, AccessLevel>     _cachedAccessLevels = new(); // 52
```

`_cachedAccessLevels` is rebuilt on every `HandleSnapshotPlayers` call from `Snapshot.Player.AccessLevel`. **It's a client-side mirror, not auth-relevant on the host** — host auth reads from `HostManager._playerRecords` directly. `TryGetAccessLevel` (l.490) and `IsOnline` (l.495) consume this cache.

### Lifecycle

```
StateManager.Awake          ── _playersManager = new GameObject("PlayersManager").AddComponent<PlayersManager>();
                              (PlayersManager has no own Awake; relies on StateManager wiring)
PlayersManager.OnClientCreated  ── client.OnRemotePlayersDidChange += OnRemotePlayersDidChange
                              (called from Multiplayer.CreateClient → StateManager.PlayersManager.OnClientCreated, l.154)

late-join / new player join
   │
   ├─ host: HostManager.PostActivate adds Snapshot.Player → SendPlayerList (broadcasts PlayerList to all)
   │
   ▼
PlayerList INetworkMessage arrives at every client
   │  ClientManager.ClientDidReceivePlayerList → fires OnRemotePlayersDidChange
   ▼
PlayersManager.OnRemotePlayersDidChange(players)
   │  HandleSnapshotPlayers(players, context=PlayerList)
   │  ├─ SplitSnapshotPlayers — diffs against _remotePlayers
   │  ├─ NotifyOfDisconnected
   │  ├─ ClearRemotePlayers          ← TEAR DOWN ALL REMOTE PLAYERS, including ones still present
   │  ├─ rebuild _cachedAccessLevels
   │  ├─ recreate every remote: CreateRemotePlayer + ConfigureAvatar
   │  ├─ NotifyOfConnected
   │  └─ Messenger.Default.Send(default(PlayersDidChange))

snapshot restore (load / late-join)
   │
   ▼
PlayersManager.RestoreFromSnapshot(players, trainCrews)
   ├─ HandleSnapshotPlayers(players, context=Restore)  ← same teardown/rebuild
   │  └─ for self: RestoreCharacterPosition(player)    ← jumps camera to last position
   └─ SetTrainCrews(trainCrews)                        ← replaces _trainCrews

OnWillUnloadMap  ── ClearRemotePlayers; unsubscribe OnRemotePlayersDidChange
OnDestroy        ── ClearRemotePlayers
```

**HIGH-VALUE FINDING — the join/leave wire diff is destructive.** `HandleSnapshotPlayers` calls `ClearRemotePlayers` (l.163) **every time a `PlayerList` arrives**. Every existing `RemotePlayer` GameObject is destroyed and recreated, even players who haven't changed. `RemoteAvatar`s lose their interpolation `_frames` buffer; the `AvatarPrefab` is freshly instantiated through `AvatarManager.Instance.AddRemote`. **A single new player joining causes every other remote player to flicker through avatar reconfig**. Patches that attach mod state to `RemotePlayer` MonoBehaviours must subscribe to `PlayersDidChange` and reattach.

### Player join / leave lifecycle (detailed, MP)

```
host                                              client
────                                              ──────
Steam connection accept (auto-true, l.220)
SetAndSendClientStatus(Initial→Anonymous)
                                                  GameClient.SendLogin(name, customization, password)
HandleMessageAnonymous: Authenticate(...)
   ├─ check _playerRecords[playerId]
   ├─ check storage.AllowNewPlayers + password
   └─ if Banned: Disconnect(AccessDenied)
SetAndSendClientStatus(Authenticated, accessLevel)
                                                  ClientManager.RequestActive() → sends RequestActive
HandlePendingRequestActive: if !_hasLoadedSnapshot, queue;
                            else SetAndSendClientStatus(Active);
                                 PostActivate(playerInfo):
   ├─ _snapshot.players[id] = Snapshot.Player(name, level, customization, position)
   ├─ UpdatePlayerRecord(playerId, ...)            ← upserts PlayerRecord
   ├─ SendSnapshotTo(playerId)                     ← sends SnapshotEnvelope
   ├─ SendPlayerList()                             ← broadcasts PlayerList to all
   ├─ SendPlayerRecords()                          ← broadcasts PlayerRecords to ≥Trainmaster
   └─ SendTo(playerId, new SetPlayerPosition(pos))
                                                  receives SnapshotEnvelope
                                                  → StateManager.PopulateFromRemoteSnapshot
                                                  → PlayersManager.RestoreFromSnapshot (sees self → RestoreCharacterPosition)
                                                  receives PlayerList (every client)
                                                  → PlayersManager.HandleSnapshotPlayers(PlayerList)
                                                    → fires PlayersDidChange Messenger event

DISCONNECT
SteamServer.OnConnectionStatusChanged → ClientDidDisconnect
ClientDidDisconnect → PlayerDidDisconnect:
   ├─ _snapshot.players.Remove(playerId.String)
   └─ SendPlayerList()                             ← broadcast
                                                  every client receives PlayerList
                                                  → PlayersManager rebuilds remotes, fires PlayersDidChange
```

**Key: there is no separate "PlayerLeft" message.** Disconnect == `PlayerList` minus that player. Mods that need precise leave events should diff successive `PlayersDidChange` events (between `PlayersManager.AllPlayers` snapshots) rather than wait for a dedicated event.

### `MyTrainCrew` (l.91)

```csharp
public TrainCrew MyTrainCrew => TrainCrews.FirstOrDefault(crew => crew.MemberPlayerIds.Contains(PlayerId));
```

**Linear scan over all crews × member sets every call.** Used in hot paths (e.g., `StateManager.Handle` for `SwitchListUpdate` checks `MyTrainCrew?.Id == switchListUpdate.TrainCrewId`). Cache externally if calling per-frame.

### `TrainCrewIdFor(PlayerId)` (l.323)

Same shape — iterates all crews, checks each `MemberPlayerIds`. Returns `null` if the player isn't on any crew. **A player can only be on one crew at a time** — `HandleRequestTrainCrewMembership` enforces this (l.348-351 removes the player from every other crew when joining).

### Patch candidates

| Method | Why patch |
|---|---|
| `PlayersManager.HandleSnapshotPlayers` | Catch every roster change. **Destructive rebuild** — patch `NotifyOfConnected`/`NotifyOfDisconnected` for additive event semantics. |
| `PlayersManager.CreateRemotePlayer` | Inject mod-side state on the new GameObject before `ConfigureAvatar`. |
| `PlayersManager.ClearRemotePlayers` | Override the destroy-everything-then-rebuild policy. |
| `PlayersManager.MyTrainCrew` getter | Provide a cache to avoid the per-call linear scan. |
| `PlayersManager.HandleRequestTrainCrewMembership` | Add side-effects (logging, broadcasts, notifications) on join/leave. Host-only path. |
| `PlayersManager.HandleRequestCreateTrainCrew` | Inject defaults (e.g., always start the creator as a member is already the case via UI; for programmatic creation patch here). |
| `PlayersManager.SetTrainCrews` | Catch the post-`UpdateTrainCrews` apply on every client. |
| `PlayersManager.UpdateCameraPosition` | Tap into per-player camera positions if you need cross-player proximity logic. |
| `PlayersManager.IsPlayerCameraNear` | Override the 60s staleness or radius semantics. |

### MP authority

- `PlayersManager` runs on every machine. Mutating methods (`HandleRequestTrainCrew*`, `HandleRequestRenameTrainCrew`) call `StateManager.AssertIsHost()` or `DebugAssertIsHost()` — see [Gotchas](#gotchas) for the difference.
- `_trainCrews` mutations on the host are **immediately followed by** `StateManager.ApplyLocal(new UpdateTrainCrews(...))` — the host doesn't write directly to clients; it round-trips through its own dispatcher (LocalGameClient loopback) and broadcasts the same way clients receive it.
- `_remotePlayers` is per-machine (host has it too, mirroring clients). Not synced; rebuilt from `Snapshot.Player` snapshots.

### Related Messenger / KVO events

| Event | Type | Sender |
|---|---|---|
| `Game.Events.PlayersDidChange` | Messenger struct (empty) | `PlayersManager.HandleSnapshotPlayers` (every roster change) |
| `Game.Events.TrainCrewsDidChange` | Messenger struct (empty) | `PlayersManager.SetTrainCrews` (every UpdateTrainCrews ingest) |
| `Game.Events.AccessLevelDidChange(old, new)` | Messenger struct | `ClientManager.ClientStatusDidChange` when `Active` is reached and the level changed |
| `Game.Events.PlayerRecordsDidChange` | Messenger struct (empty) | `StateManager.Handle` when `PlayerRecords` arrives (Trainmaster+ only) |
| `Game.Events.CarTrainCrewChanged(carId)` | Messenger struct | `TrainController.HandleSetCarTrainCrew` after assignment |

### Gotchas

- **`HandleRequestCreateTrainCrew` calls `StateManager.DebugAssertIsHost`**, not `AssertIsHost`. `DebugAssertIsHost` is **empty** (`StateManager.cs:1355`) — see [State Manager § AssertIsHost](state-manager.md#gotchas). A misrouted client-side call would silently mutate `_trainCrews` then `ApplyLocal(UpdateTrainCrews)` which the host *would* see and broadcast back. In practice the message-level `[MinimumAccessLevel(Trainmaster)]` gate on `RequestCreateTrainCrew` blocks this, but the assertion is not defense-in-depth.
- **Train-crew name uniqueness is enforced only at create**, not at rename (`HandleRequestRenameTrainCrew`, l.401-426). Two crews can end up with the same name if a rename collides post-creation.
- **`/crew create` console command always adds the local player** to the new crew (`CrewCommand.cs:59`) — different from the UI which can create empty crews. Mods scripting crew creation via `RequestCreateTrainCrew` choose membership.
- **Crew IDs are generated host-side** by `IdGenerator.TrainCrew.Next()` (l.382). Clients submitting `RequestCreateTrainCrew` provide an empty `Id`; the host overwrites. Don't assume the `Id` you sent is the one you get back — listen to `TrainCrewsDidChange` and look up by name.
- **Renaming a train crew does NOT update its switch list keying.** `OpsController.SwitchListController` keys switch lists by `trainCrewId`, not name (`StateManager.cs:1245`); renames preserve the relationship. But `Multiplayer.Broadcast` text uses the *old* name in the announcement, then the new (`PlayersManager.cs:420`).
- **`HandleRequestRenameTrainCrew` skips the auth check that the message attribute provides**, because the dispatch path runs only on host (`StateManager.cs:787-789`). The `[MinimumAccessLevel(Trainmaster)]` on `RequestEditTrainCrew` is the only gate.
- **`NotifyOfConnected`'s "X has connected" message style depends on `_hasNotifiedOfPlayers || StateManager.IsHost`** (l.248). The very first PlayerList received as a client uses "X is connected" (present-tense, batched); subsequent ones use "X has connected" (past-tense, per-event). On the host, every event uses the past-tense form.
- **`_lastKnownPositions` is local-machine cache.** Host has its own (from clients sending `UpdateCameraPosition` via `PlayersManager.UpdateCameraPosition`); each client also accumulates its own from `UpdateCameraPosition` messages. **`IsPlayerCameraNear` only sees positions reported during this session** — staleness is 60s.
- **`RestoreCharacterPosition` jumps the local camera on snapshot restore.** Re-loading a save mid-session (host-side) jumps the host's camera back to the saved position, possibly mid-train-ride. Mods doing in-place state restores must avoid this or fork the snapshot.
- **`_localPlayer` is `default(LocalPlayer)`** but the field is `readonly`. Patching the local player struct is impractical; subclass `LocalPlayer` is impossible (sealed-ish via being a struct). Use the `IPlayer` interface boundary and patch `LocalPlayer.GamePosition` if you need to.

---

## `Game.State.TrainCrew`

```csharp
public class TrainCrew {                              // Game.State/TrainCrew.cs:7
    public string             Id;                      // host-generated
    public string             Name;
    public string             Description;
    public HashSet<PlayerId>  MemberPlayerIds;        // can be empty
    public string             TimetableSymbol;        // null = not a timetable train

    public TrainCrew(Snapshot.TrainCrew snapshot);    // ctor from wire
    public Snapshot.TrainCrew ToSnapshot();           // → wire
}
```

That's the whole class. **No methods, no events, no behaviour** — pure data container. `TrainCrew` instances are created fresh from `Snapshot.TrainCrew` on every `SetTrainCrews` call. **Holding a long-lived `TrainCrew` reference is a bug** — it'll be replaced by a new instance the next time `UpdateTrainCrews` arrives. Look up by `Id` via `PlayersManager.TrainCrewForId` instead.

### `Snapshot.TrainCrew` (the wire format)

```csharp
[MessagePackObject(false)]
public struct TrainCrew(string id, string name, HashSet<string> memberPlayerIds, string description, string timetableSymbol)
{
    [Key(0)] public string          Id;
    [Key(1)] public string          Name;
    [Key(2)] public HashSet<string> MemberPlayerIds;        // strings, not PlayerIds
    [Key(3)] public string          Description;
    [Key(4)] public string          TimetableSymbol;
}
```

Five MessagePack keys. Adding a sixth requires bumping the snapshot version semantically (currently `Snapshot.Version = 1`, never checked).

### Patch candidates

| Method | Why patch |
|---|---|
| `TrainCrew` ctor / `ToSnapshot` | Add mod-side fields. **You must add storage outside the struct** (e.g., a parallel `Dictionary<string, ModCrewExtension>` keyed by `Id`) — extending the wire struct without coordinated server/client patches breaks deserialization. |

### Gotchas

- **`MemberPlayerIds` is `HashSet<PlayerId>` in the runtime class but `HashSet<string>` in the snapshot.** The conversion is in the ctor and `ToSnapshot`. Don't mix them.
- **Empty crews are allowed.** `_orderedTrainCrews` includes crews with zero members. The UI displays them as joinable.
- **`TimetableSymbol` is nullable.** `null` and empty-string are coerced equivalent (`HandleRequestSetTrainCrewTimetableSymbol` l.435 normalizes empty → null).

---

## Train-crew → car relationship

```csharp
// Model/Car.cs
public string trainCrewId;                            // 226 — public mutable field, NOT KVO
```

Cars hold a `trainCrewId` string. Set in `Car.Setup` from `CarDescriptor.TrainCrewId` (l.999), updated by `TrainController.HandleSetCarTrainCrew` (l.2030):

```csharp
public void HandleSetCarTrainCrew(IPlayer sender, string carId, string trainCrewId) {
    Car car = CarForId(carId);
    string oldId = car.trainCrewId;
    car.trainCrewId = trainCrewId;
    Messenger.Default.Send(new CarTrainCrewChanged(car.id));
    if (car is SteamLocomotive sl && sl.TryGetTender(out var tender)) {
        tender.trainCrewId = trainCrewId;             // tender follows engine
        Messenger.Default.Send(new CarTrainCrewChanged(tender.id));
    }
    if (IsHost) { ... Multiplayer.Broadcast(...) ... }
}
```

Wire format: `SetCarTrainCrew(carId, trainCrewId)` (`Game.Messages/SetCarTrainCrew.cs`, `[MinimumAccessLevel(Trainmaster)]`, Union 406). Runs on both host and clients (no `IsHost` gate at the entry; the broadcast announcement is host-gated). The host records the value in `_snapshot.Cars[carId].TrainCrewId` (`HostManager.cs:909-913`).

**Critical: `trainCrewId` is NOT a KVO key.** It's a plain field. Late-joiners get the value via `Snapshot.Car.TrainCrewId` (`Snapshot.cs:96`), which `Car.Setup` reads through `CarDescriptor.TrainCrewId`. Mid-session changes flow only through `SetCarTrainCrew` messages — there's no PropertyChange route, no per-key auth on this field, no observer pattern.

### Steam-locomotive tender coupling

`HandleSetCarTrainCrew` automatically copies the trainCrewId to the coupled tender (l.2036-2040). **The tender does NOT participate in the auth check** — assigning a steam loco to a crew silently reassigns the tender. If the tender is decoupled and recoupled, its `trainCrewId` persists from the last assignment, NOT from re-inheriting the engine's. Diesel locomotives have no analog.

### Patch candidates

| Method | Why patch |
|---|---|
| `TrainController.HandleSetCarTrainCrew` | Add side-effects (notifications, KVO mirror, signal-system updates). |
| `Car.Setup` | Catch initial assignment from `CarDescriptor.TrainCrewId`. |

### Gotchas

- **`Car.trainCrewId` may reference a deleted crew.** `HandleRequestDeleteTrainCrew` removes the crew but **does not clear the carId on cars**. Cars then have a dangling `trainCrewId` that `MinimumLevelCrew` auth treats as "membership unknown" → falls through to `return true` (`StateManager.cs:1407`). Effectively, deleting a crew makes its cars Crew-accessible to any Crew+ player. Patch `HandleRequestDeleteTrainCrew` to scrub car assignments if you want different behavior.
- **Car-crew assignment is Trainmaster** even when crew membership is self-managed. Players can join/leave crews but cannot reassign equipment to them — that's a separate axis.
- **No multi-crew assignment.** `Car.trainCrewId` is a single string; a car belongs to at most one crew (or none).

---

## The `Crew` access-level + train-crew membership gate

`AccessLevel.Crew = 20` (`Game.AccessControl/AccessLevel.cs`). Conceptually "engineer" — can operate equipment, throw switches, manage waybills via the inspector. Above `Passenger` (10), below `Dispatcher` (30).

**Membership is a separate gate that only kicks in when `_storage.TrainCrewMembershipRequired` is true.**

```csharp
// StateManager.SenderSatisfiesAuthorizationRequirement, l.1393
case AuthorizationRequirement.MinimumLevelCrew:
{
    if (senderAccessLevel < AccessLevel.Crew) return false;
    if (senderAccessLevel >= AccessLevel.Trainmaster) return true;     // Trainmaster bypass
    if (_storage.TrainCrewMembershipRequired
        && requirement.Object is string trainCrewId
        && _playersManager.TrainCrewForId(trainCrewId, out var trainCrew))
    {
        return trainCrew.MemberPlayerIds.Contains(senderPlayerId);
    }
    return true;                                                       // permissive default
}
```

**The train-crew check is Crew-only on cars.** It composes with `Car.AuthorizationRequirementForPropertyWrite`'s default branch (`Car.cs:3146`):

```csharp
return new AuthorizationRequirementInfo(AuthorizationRequirement.MinimumLevelCrew, trainCrewId);
```

The `trainCrewId` carried as `requirement.Object` is the car's `trainCrewId`. So:

| Car's `trainCrewId` | `TrainCrewMembershipRequired` | Sender level | Sender on crew? | Allowed? |
|---|---|---|---|---|
| any | false | Crew | n/a | **yes** |
| `null` | true | Crew | n/a | **yes** (no crew → no membership check possible; falls through) |
| `"abc123"` | true | Crew | yes | **yes** |
| `"abc123"` | true | Crew | no | **no** |
| `"abc123"` | true | Trainmaster+ | n/a | **yes** (bypass at line 1399) |
| any | any | Passenger | n/a | **no** (level < Crew) |

**Edge case: a `trainCrewId` pointing at a deleted crew.** `TrainCrewForId` returns false → falls through to the default `return true`. Effectively unprotected. (See [§ Train-crew → car relationship Gotchas](#gotchas-2).)

The same `MinimumLevelCrew + trainCrewId` pattern applies to:
- All non-prefixed Car KVO keys (default branch in `Car.AuthorizationRequirementForPropertyWrite`).
- `RequestSetTrainCrewMembership` for **self-toggle** (`RequestSetTrainCrewMembershipRuleAttribute.cs:22`): `senderAccessLevel >= AccessLevel.Crew`.

**`TrainCrewMembershipManagedByTrainmaster`** is a stricter mode (`GameStorage.cs:225`) — when true, even self-toggle of crew membership requires Trainmaster (`RequestSetTrainCrewMembershipRuleAttribute.cs:18`). Use cases: lobby-managed games where the server admin assigns roles.

**Two toggles, four modes:**

| `TrainCrewMembershipRequired` | `TrainCrewMembershipManagedByTrainmaster` | Behaviour |
|---|---|---|
| false | false | Crew is a flat permission. Anyone Crew+ can drive any equipment. (Default) |
| true | false | Crew+ on the crew can drive crew's cars. Crew+ can self-join/leave any crew. |
| false | true | Crew is flat, but only Trainmaster+ assigns players to crews. (Strange but legal.) |
| true | true | Strict: Crew+ on crew can drive crew's cars. Trainmaster+ controls who's on what. |

Both settings are on `_game` KVO. UI: `UI.CompanyWindow/SettingsPanelBuilder.cs:409-417`.

### Gotchas

- **The `MinimumLevelCrew` gate is permissive when the car has no crew.** A car with `trainCrewId = null` is freely operable by any Crew+ regardless of `TrainCrewMembershipRequired`. To force "everyone must be on a crew," patch `Car.AuthorizationRequirementForPropertyWrite` to inject a synthetic crew or to change the auth requirement.
- **Trainmaster bypass is unconditional.** Even with `TrainCrewMembershipManagedByTrainmaster=true`, a Trainmaster operating someone else's car for them won't fail auth — only `RequestSetTrainCrewMembership` on others is gated.
- **The `requirement.Object` channel is `object`-typed.** `AuthorizationRequirementInfo.Object` is loosely-typed for extension. Mods using the same `MinimumLevelCrew` requirement with a non-string `Object` will hit the `is string` check and fall through to permissive.
- **Membership doesn't affect physics or AI.** A non-member with no Crew level can stand in the cab and look at gauges; they just can't twist the throttle. The auto-engineer doesn't check membership at all.

---

## `HostManager._playerRecords` and PlayerRecord persistence

```csharp
private Dictionary<PlayerId, PlayerRecord> _playerRecords = new();   // HostManager.cs:147
private readonly HashSet<Client> _queueForBannedDisconnect = new();   // 149

public struct PlayerRecord {                          // Game.Persistence/PlayerRecord.cs
    [Key("name")]               public string      Name;
    [Key("position")]           public CharacterPosition Position;
    [Key("updated")]            public DateTime    Updated;
    [Key("steamId")]            public ulong       SteamId;
    [Key("accessLevel")]        public AccessLevel AccessLevel;
    [Key("accessLevelChanged")] public DateTime    AccessLevelChanged;
    [Key("lastConnected")]      public DateTime    LastConnected;
}
```

**Saved with the world**, but in a sidecar dictionary, not in the `Snapshot`. `SaveManager.Save` writes `PlayerRecordsForSave()` (`HostManager.cs:753`); load supplies it to `LoadSnapshot(snapshot, playerRecords, carBodyPositions)` (l.722).

### Lifecycle

- **Created on first successful login** by `HostManager.PostActivate` → `UpdatePlayerRecord` (l.621). Even passengers get a record.
- **Updated on every `AddUpdateCharacter`** (RecordState l.1066) — the name is sanitized and stored.
- **Updated on every `UpdateCharacterPosition`** (RecordState l.1082) — last-known position persisted.
- **Updated on `SetAccessLevel`** (l.1106) — bumps `AccessLevelChanged`.
- **Removed by `RemovePlayerRecord`** (l.1159) — President-only, refuses online players.

`PlayerRecordsForSave()` returns all records as a `Dictionary<string, PlayerRecord>` keyed by stringified PlayerId, no filtering. Banned players are saved.

### `PlayerRecords` IGameMessage

```csharp
[HostOnlyAuthorizationRule]
public struct PlayerRecords(Dictionary<string, PlayerRecord> records) : IGameMessage  // Union 30
```

Sent by `HostManager.SendPlayerRecords()` to **clients with AccessLevel ≥ Trainmaster** (`SendToAccessLevelAndUp(Trainmaster, ...)`, l.698). Triggers:
- Every `PostActivate` (so newly-Active Trainmaster+ gets the records).
- Every `SetAccessLevel` change.
- Every `RemovePlayerRecord`.

Client-side handling (`StateManager.cs:665-670`): builds/replaces a `PlayerRecordsClientManager` and fires `PlayerRecordsDidChange`. Trainmaster demoted below Trainmaster → `OnAccessLevelDidChange` clears `PlayerRecordsClientManager` (`StateManager.cs:330-333`) — security-sensitive data is dropped.

### `RequestSetAccessLevel` & `RemovePlayerRecord`

```csharp
[RequestSetAccessLevelRule]                    // see RequestSetAccessLevelRuleAttribute.cs
public struct RequestSetAccessLevel(string recordKey, AccessLevel accessLevel) : IGameMessage  // Union 31

[MinimumAccessLevel(AccessLevel.President)]
public struct RemovePlayerRecord(string recordKey) : IGameMessage  // Union 32
```

`RequestSetAccessLevelRuleAttribute.CheckAuthorization`:
```
target accessLevel               required senderAccessLevel
─────────────────                ─────────────────────────
Crew or below (Banned/Passenger/Crew/Dispatcher) → Trainmaster
Trainmaster                                       → Trainmaster
Officer                                           → Officer
President                                         → President
```

Note the asymmetry: **demoting someone from Trainmaster requires Trainmaster** (you can demote your peers). Banning is `target=Banned` → requires Trainmaster. There is **no rule preventing a Trainmaster from banning the President** — but wait, the *target's* access level is what gates, not the sender's seniority over the target. The `SetAccessLevel(playerId=...)` call early-returns if `HostPlayerId == playerId` (l.1109-1113), so the *host* can't be banned, but a non-host President can be banned by a Trainmaster. **HIGH-VALUE FINDING.** Mods enforcing rank ordering (no peer demotion) must add their own check.

`SetAccessLevel` flow (host-side, l.1106):
1. Reject if target is host's own PlayerId.
2. Update record's `AccessLevel` + `AccessLevelChanged`.
3. `AnnounceAccessChange` → broadcast "X has banned/promoted/demoted Y to Z".
4. For each Active client matching the target PlayerId: update `PlayerInfo.AccessLevel`, send `ClientStatus(Active, newLevel)` (which fires `AccessLevelDidChange` client-side).
5. If `targetAccessLevel == AccessLevel.Banned`: add client to `_queueForBannedDisconnect`. **Drained at the end of the next `HandleMessage` call** (l.264-268), not immediately. So a banned player sees their access level demote first, then disconnect.
6. `SendPlayerRecords()` to all Trainmaster+.

`RemovePlayerRecord` (l.1159):
- Throws `Exception("Record not found")` if missing.
- `Multiplayer.SendError(sender, "Can't remove online player record.")` and aborts if the player is online.
- Otherwise removes and re-sends `PlayerRecords`.

### Patch candidates

| Method | Why patch |
|---|---|
| `HostManager.Authenticate` | Custom auth — external whitelist, group membership lookup, IP filtering. Currently password + record-based. |
| `HostManager.PostActivate` | Hook into the "this client is now Active" moment. After this returns, the client is fully online. |
| `HostManager.SetAccessLevel` | Add audit logging, additional gate (peer-demote prevention), kick-on-demote. |
| `HostManager.RemovePlayerRecord` | Custom semantics (e.g., archive instead of delete). |
| `HostManager.UpdatePlayerRecord` | Tap into every record write — name change, position update, login. |
| `HostManager.PlayerDidDisconnect` | Hook for "this player just left" — fires before `SendPlayerList` so listeners can use the about-to-be-removed entry. |
| `HostManager.ValidateUsername` | **Currently always returns `true`** (l.612, even on duplicate detection logs as `Error`). Patch here to actually reject. See [Multiplayer Core § Authentication](multiplayer-core.md#authentication-hostmanagerauthenticate-hostmanagercs535). |

### MP authority

- All record state lives **only on the host**. Clients only see records if Trainmaster+ via `PlayerRecords` broadcast.
- `RequestSetAccessLevel` and `RemovePlayerRecord` go through normal message dispatch with the auth attributes above.
- No KVO involvement — `PlayerRecord` is not a KeyValueObject. Cannot be observed via `KeyValueObject.Observe`. Subscribe to `PlayerRecordsDidChange` Messenger event instead.

### Gotchas

- **`SetAccessLevel(host)` is a no-op with a warning** — host can never have their access level changed. The host is always implicitly President.
- **Banned-player records persist.** The record stays with `AccessLevel.Banned`; `Authenticate` reads it and disconnects them with `AccessDenied`. To "unban," the President must `RequestSetAccessLevel(playerId, AccessLevel.Passenger)` (or higher). Removing the record (`RemovePlayerRecord`) also un-bans — they're treated as a new player on next login.
- **`HostManager.ShouldAcceptConnection` always returns `true`** (`HostManager.cs:222`) — banned-player handling happens after Login, not at connection accept. A spammy reconnect loop from a banned Steam ID will burn auth-handshake CPU on the host. See [Multiplayer Core § Steamworks P2P transport](multiplayer-core.md#steamworks-p2p-transport).
- **`_queueForBannedDisconnect` is drained synchronously at the end of `HandleMessage`.** If you ban during a non-message tick (e.g., a console command), the queue won't drain until the next inbound message. Force a drain by calling something that triggers `HandleMessage`, or patch `SetAccessLevel` to dispatch the disconnect inline.
- **`PostActivate` sends `Snapshot` then `PlayerList` then `PlayerRecords`** — three large messages back-to-back. On slow connections, the order matters: late-joining client sees Snapshot (own self), then PlayerList (knows about all players), then PlayerRecords (Trainmaster+). Don't assume PlayerRecords arrives before PlayerList for new Trainmasters.
- **`AccessLevelDidChange` on the host fires only when level *changes during Active***. Promoting yourself via console (host's level is hardcoded President) does nothing; `OnAccessLevelDidChange` on the host runs only if it's somehow demoted (which it never is).
- **`InitialCharacterPosition` reuses the saved position** if the record has a valid one (`HostManager.cs:641`). New players get the default spawn. After a player has connected once, they always respawn at their last logout position — even after the host loaded a save from earlier.

---

## "What happens if the only Officer disconnects?"

- Their `PlayerRecord.AccessLevel` stays at `Officer` (records persist on disconnect).
- `_snapshot.players` removes the entry; their `Snapshot.Player.AccessLevel` mirror is gone.
- They reconnect → `Authenticate` finds their record → returns `Officer` → they're Officer again.
- **Officer-only properties (`OfficerPrefixes` on `Car` includes `ops.sell-dest`) become unwritable for everyone.** The host (President) can still write them; remaining Trainmasters+ cannot.
- No automatic promotion. The President (host or another non-host President if any) must `RequestSetAccessLevel(playerId, Officer)` on someone else.
- If the host is the only Officer (host is always President which is ≥ Officer), nothing changes — host can always do Officer things.

## "Crew assigned to a train on host but train on a different set?"

- `Car.trainCrewId` is per-car, persisted via `Snapshot.Car.TrainCrewId`.
- `IntegrationSet` membership is determined by physical coupling, **not** by trainCrewId.
- A crew can be assigned to cars across multiple `IntegrationSet`s. `MyTrainCrew` is the crew, not "the train I'm on."
- The UI's "Equipment" list (`CrewsPanelBuilder.cs:139-152`) shows all cars matching `car.trainCrewId == trainCrew.Id` regardless of consist.
- `OpsController.SwitchListController.SendSwitchListUpdate(trainCrewId)` (called in `HandleRequestTrainCrewMembership`, l.361) sends the crew-scoped switch list — but if the crew's cars are split across two consists, the switch list still treats them as one logical "train" for the crew's worklist. **Switch lists are per-crew, not per-set.**

## "What happens if a crew is deleted while a player is on it?"

- `HandleRequestDeleteTrainCrew` (`PlayersManager.cs:389`) removes the crew from `_trainCrews`. **Does not touch `Car.trainCrewId` or any player's membership** (membership lived inside `MemberPlayerIds`, which goes away with the crew).
- `MyTrainCrew` for any ex-member returns `null` next call.
- Cars formerly assigned to that crew now have a dangling `trainCrewId` → permissive Crew auth (see [§ The `Crew` access-level gate gotchas](#gotchas-3)).
- `Multiplayer.Broadcast` announces the deletion; ex-members are not individually notified.

---

## Player nicknames & color schemes

### Nicknames

- Local: `Preferences.MultiplayerClientUsername` (PlayerPrefs key, see [Settings & Preferences](settings-preferences.md)).
- Sent at: `Login` (initial), `AddUpdateCharacter` (after Active, on lantern toggle, on customization change).
- Stored at: `_snapshot.players[id].Name`, `_playerRecords[id].Name`.
- Sanitized: `StringSanitizer.SanitizeName` strips bad chars; null/whitespace **passes through** (logged but accepted, `HostManager.cs:599-602`).
- **Duplicate-name validation is a no-op** — `ValidateUsername` always returns `true` (l.612). Two players with the same name connect fine. Mods enforcing uniqueness must patch `ValidateUsername` or `Authenticate`.
- Display: `PlayersManager.NameForPlayerId(playerId)` checks live players first, then PlayerRecords (Trainmaster+ only sees this fallback). Returns `"Unknown"` otherwise (l.144).

### Color schemes (per-car, not per-player)

There is **no per-player color scheme**. Cars have an `_colorScheme` KVO key (per-car) — see [Cars & Cargo § KVO key map](cars-cargo.md). It is in `Car.TrainmasterPrefixes` (`Car.cs:471`), so writes require Trainmaster.

**Prefix-collision oddity:** `_colorScheme` starts with `_`, which is also the `HostPrefixes` prefix marker (HostOnly). The auth resolver iterates **Officer → Trainmaster → Passenger → Host** (`Car.cs:3114-3142`); `_colorScheme` matches the Trainmaster prefix list **first**, so the HostOnly catch is bypassed. Same trick for `lettering.basic` and `whistle.custom` (Trainmaster prefixes that don't start with `_` so it doesn't matter), and `_colorScheme` (Trainmaster prefix that DOES start with `_` and thus dodges HostOnly). **Adding a new mod-side car KVO key starting with `_` will be HostOnly unless you patch `Car.AuthorizationRequirementForPropertyWrite` or add a more-specific Trainmaster/Officer prefix.**

### Avatar customization (per-player visual identity)

- `Snapshot.CharacterCustomization` is the wire format (a `Dictionary<string, IPropertyValue>` blob).
- `AvatarDescriptor` is the runtime form (`Avatar/AvatarDescriptor.cs`): `Gender`, `SkinToneIndex`, `Accessories` dict.
- Stored in `Preferences.AvatarDescriptor` (PlayerPrefs key `avatar.descriptor`).
- Broadcast via `AddUpdateCharacter` (`MinimumAccessLevel(Passenger)`, also `ICharacterMessage`).
- Lantern is an "accessory" in the customization dict — toggling it sends a full `AddUpdateCharacter` (see [Player & Camera § LocalAvatar](player-camera.md#characterlocalavatar-visible-avatar-for-the-local-player)).

**No mod-side hook for adding accessories.** The accessory dict is open-ended (KeyValue dict), but the avatar prefab only spawns visuals for accessories the prefab knows about. Custom accessories require asset pack additions.

---

## Per-player KVO

**There is none in vanilla.** Player-related KVO is global (`_game` keys for membership-required, etc.) or per-car (`Car` KVO with `trainCrewId` as the auth-context string). The only per-player state objects are:

- `Snapshot.Player` — sent in PlayerList, not KVO.
- `PlayerRecord` — host-side dict, not KVO.
- `_lastKnownPositions` — cache, not KVO.

The closest analog is the `PlayerProperties` system (`Game.PlayerProperties.PlayerPropertiesManager`), which holds per-player UI state (`SelectedCarId`, etc.) — but it's local-only, not synced. Used in `TrainController.PostRestoreProperties` (l.2062-2065) to restore the local player's selected car after load.

If a mod wants per-player synced state, the patterns are:
1. **Register a per-player KVO object** with `id = "player." + playerId.String`. Auth via a custom `IPropertyAccessControlDelegate` that returns `PlayerIdKey` requirement (only that player can write). The host can write any. Snapshot fan-out works automatically.
2. **Custom `IGameMessage` type** with `[Union(N)]` and route through `StateManager.Handle`'s patched dispatcher.
3. **Piggyback on `_game` KVO** with mod-prefixed keys (e.g., `mod.<modid>.<playerid>.<key>`). Subject to `_game`'s default Trainmaster auth unless overridden.

See [State Manager § RegisterPropertyObject](state-manager.md#registerpropertyobject--late-registration) for the registration mechanics.

---

## Wire format & MP authority summary

| Action | Initiator level | Message | Auth | Channel |
|---|---|---|---|---|
| Set local avatar customization | Passenger+ | `AddUpdateCharacter` | `[MinimumAccessLevel(Passenger)]` | reliable |
| Update character position | Passenger+ | `UpdateCharacterPosition` | `[MinimumAccessLevel(Passenger)]` | unreliable (Movement) |
| Update camera position | Passenger+ | `UpdateCameraPosition` | `[MinimumAccessLevel(Passenger)]` | unreliable (Movement) |
| Create train crew | Trainmaster+ | `RequestCreateTrainCrew` | `[MinimumAccessLevel(Trainmaster)]` | reliable |
| Delete train crew | Trainmaster+ | `RequestDeleteTrainCrew` | `[MinimumAccessLevel(Trainmaster)]` | reliable |
| Edit train crew (name, desc) | Trainmaster+ | `RequestEditTrainCrew` | `[MinimumAccessLevel(Trainmaster)]` | reliable |
| Set timetable symbol | Trainmaster+ | `RequestSetTrainCrewTimetableSymbol` | `[MinimumAccessLevel(Trainmaster)]` | reliable |
| Join/leave own crew | Crew+ | `RequestSetTrainCrewMembership` | `[RequestSetTrainCrewMembershipRule]` (self=Crew, other=Trainmaster, or Trainmaster always if managed) | reliable |
| Assign car to crew | Trainmaster+ | `SetCarTrainCrew` | `[MinimumAccessLevel(Trainmaster)]` | reliable |
| Update train crews dict | Host | `UpdateTrainCrews` | `[HostOnlyAuthorizationRule]` | reliable |
| Set access level (promote/demote/ban) | varies | `RequestSetAccessLevel` | `[RequestSetAccessLevelRule]` (depends on target level) | reliable |
| Remove player record | President | `RemovePlayerRecord` | `[MinimumAccessLevel(President)]` | reliable |
| Send player records | Host | `PlayerRecords` | `[HostOnlyAuthorizationRule]` (sent to ≥Trainmaster only) | reliable |
| Send player list | Host | `PlayerList` (`INetworkMessage`) | host-broadcast | reliable |
| Authentication | Steamworks | `Login` (`INetworkMessage`) | n/a (handshake) | reliable |

See [Request Messages](request-messages.md) for the canonical catalog.

---

## Cross-references

- [Multiplayer Core § Connection lifecycle](multiplayer-core.md#connection-lifecycle-networkserverclientstatus) — Initial → Hello → Login → Authenticated → Active FSM that gates this whole system.
- [Multiplayer Core § Authentication](multiplayer-core.md#authentication-hostmanagerauthenticate-hostmanagercs535) — `HostManager.Authenticate` flow that produces the AccessLevel.
- [State Manager § Auth resolver](state-manager.md#auth-resolver) — `SenderSatisfiesAuthorizationRequirement` including the `MinimumLevelCrew` + train-crew-membership composite check.
- [State Manager § Players, characters, train crews](state-manager.md#players-characters-train-crews) — dispatcher-side handler routing.
- [Player & Camera § Game.IPlayer / Game.LocalPlayer / Network.Client.RemotePlayer](player-camera.md#gameiplayer--gamelocalplayer--networkclientremoteplayer) — the visual-side counterpart of the player handles.
- [Player & Camera § RemoteAvatar](player-camera.md#avatarremoteavatar-other-players-bodies-mp) — the interpolation pipeline `RemotePlayer.UpdateAvatarPosition` feeds.
- [Cars & Cargo § KVO key map](cars-cargo.md) — per-car keys and their auth prefixes (where `_colorScheme` Trainmaster-overrides the `_` HostOnly default).
- [Couplers § Wire format & MP authority summary](couplers.md#wire-format--mp-authority-summary) — example of `Car.AuthorizationRequirementForPropertyWrite` resolving the default `MinimumLevelCrew + trainCrewId` requirement.
- [Settings & Preferences](settings-preferences.md) — `MultiplayerClientUsername`, `AvatarDescriptor` PlayerPrefs.
- [Request Messages](request-messages.md) — full per-message catalog.
