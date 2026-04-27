# Alerts, Notices & Modals — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Multiplayer Core](multiplayer-core.md) · [Hyperlink & EntityReference](hyperlink-entityref.md) · [Daily Reports](daily-reports.md) · [Console Commands](console-commands.md) · [Tutorial](tutorial.md) · [Access Control](access-control.md)

Railroader's user-facing notification family is **four parallel surfaces** that share almost no code:

1. **`Multiplayer.Broadcast(string)` / `Multiplayer.SendError(player, msg)`** — the system-message pipe. Wraps a string in a `Network.Messages.Alert(AlertStyle, AlertLevel, msg, ts)` and either presents it locally (singleplayer) or fans out via `HostManager.SendToAll` / `SendTo`. Always host-emit.
2. **`UI.Console.Console.AddLine`** — the chat/console buffer. Receives `AlertStyle.Console` alerts, but is also called locally by `Console.Log` (no wire).
3. **`UI.Common.Toast.Present`** — singleton on-screen ephemeral text balloon. Receives `AlertStyle.Toast` alerts, also called directly by gameplay code (`LinkDispatcher` "Car not found" toast).
4. **`Game.Notices.NoticeManager.PostEphemeral` / `PostEphemeralLocal`** — entity-keyed cards in the right rail of the HUD. Sent across the wire via the `PostNoticeEphemeral` `IGameMessage` (HostOnly) — clients receive and call `PostEphemeralLocal`.

Plus the modal layer:

5. **`UI.Common.ModalAlertController`** — singleton blocking dialog. Three entry points: `PresentOkay(title, msg, onOkay)`, `Present<T>(title, msg, buttons, onButton)` (with optional input string), and `Present(Action<UIPanelBuilder, Action>)` (free-form). **Local-only — never replicated. No queue, no stack, no dismiss-by-ID. Each `Present` instantiates a new `ModalAlert` MonoBehaviour as a child of the singleton's canvas — multiple modals can be on screen simultaneously and dismiss independently in arbitrary order.**

The four notification surfaces are routed through `WindowManager.Present(Alert)` (the *only* `Alert`-receiver) which switches on `AlertStyle.Toast` → `Toast.Present` and `AlertStyle.Console` → `Console.shared.AddLine`. Hyperlink clickability **diverges by surface**: console lines have a `TextLinkReceiver` baked into the prefab; toasts do **not** (so toast hyperlinks render visually but are inert); notice cards do (per the asset, not in code); modal panels do (via `UIPanelBuilder.AddTextLinkReceiverIfNeeded`'s `<link` substring trigger).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Multiplayer.Broadcast(string)` | `Network/Multiplayer.cs:181` | Fan-out chat-style alert to all clients (Console style, Info level) |
| `Multiplayer.SendError(IPlayer, string, AlertLevel=Error)` | `Network/Multiplayer.cs:167` | Single-target Toast alert (defaults to Error level → middle position) |
| `Network.Messages.Alert` (`INetworkMessage`, Union 12) | `Network.Messages/Alert.cs:6` | `(AlertStyle, AlertLevel, string Message, double Timestamp)` wire form |
| `Network.Messages.AlertStyle` enum | `Network.Messages/AlertStyle.cs:3` | `Toast`, `Console` |
| `Network.Messages.AlertLevel` enum | `Network.Messages/AlertLevel.cs:3` | `Info`, `Error` |
| `HostManager.SendToAll(INetworkMessage)` | `Game/HostManager.cs:504` | Fan-out (delegates to `SendToAllExcept(msg, null)`) |
| `HostManager.SendToAllExcept(msg, except)` | `Game/HostManager.cs:509` | Fan-out skipping one player |
| `HostManager.SendTo(PlayerId, INetworkMessage)` | `Game/HostManager.cs:437` | Single-target |
| `HostManager.SendTo(HashSet<PlayerId>, msg)` | `Game/HostManager.cs:450` | Multi-target |
| `WindowManager.Present(Alert)` | `UI.Common/WindowManager.cs:91` | The only `Alert` receiver — switches on style |
| `Toast.Present(string, ToastPosition=Middle)` | `UI.Common/Toast.cs:19` | Singleton via `FindObjectOfType` |
| `Console.shared.AddLine(string, GameDateTime)` | `UI.Console/Console.cs:107` | Append to chat buffer |
| `NoticeManager.PostEphemeral(EntityReference, key, content)` | `Game.Notices/NoticeManager.cs:48` | Host-emit notice card via `PostNoticeEphemeral` IGameMessage |
| `NoticeManager.PostEphemeralLocal(EntityReference, key, content)` | `Game.Notices/NoticeManager.cs:66` | Local-only notice card; the actual UI mutator |
| `NoticeManager.Handle(PostNoticeEphemeral)` | `Game.Notices/NoticeManager.cs:60` | Network receive → forward to `PostEphemeralLocal` |
| `Game.Messages.PostNoticeEphemeral` (`HostOnly`) | `Game.Messages/PostNoticeEphemeral.cs:8` | `(SerializableEntityReference, string Key, string Content)` |
| `ModalAlertController.PresentOkay(title, msg, onOkay=null)` | `UI.Common/ModalAlertController.cs:97` | Single-button modal |
| `ModalAlertController.Present<T>(title, msg, buttons, onButton)` | `UI.Common/ModalAlertController.cs:27` | Multi-button modal |
| `ModalAlertController.Present(Action<UIPanelBuilder,Action>)` | `UI.Common/ModalAlertController.cs:119` | Free-form panel modal |
| `Game.Events.RequestRejected` (struct) | `Game.Events/RequestRejected.cs:6` | Empty Messenger struct fired host-side after most `SendError` calls |

---

## Spine 1: an `Alert` from emit to render

```
HOST                                                CLIENT
─────                                                ──────
Multiplayer.Broadcast("Foo")                          (any active client)
  Alert a = new Alert(Console, Info,
                      "Foo", TimeWeather.Now.TotalSeconds);
  if (Host == null)                                   ← singleplayer-only branch
      WindowManager.Shared.Present(a);                ← local present, no wire
  else
      Host.SendToAll(a);   ─────────────►            GameClient.HandleMessage:
                                                        case Alert →
                                                          ClientDelegate.ClientDidReceiveAlert(alert)
                                                            (= ClientManager.cs:249)
                                                            WindowManager.Shared.Present(alert)
                                                              switch alert.Style:
                                                                Toast   → Toast.Present(msg,
                                                                            level==Error ? Middle : Bottom)
                                                                Console → Console.shared.AddLine(msg,
                                                                            new GameDateTime(alert.Timestamp))

Multiplayer.SendError(player, "Bar")                  (single-target)
  Alert a = new Alert(Toast, Error, "Bar", ts);
  if (player.IsRemote)
      Host.SendTo(player.PlayerId, a);  ────────────► (same path, single recipient)
  else
      WindowManager.Shared.Present(a);                ← local present (sender is host themselves)
```

**Three load-bearing observations:**

1. **`Multiplayer.Broadcast` requires a `Host` to fan out.** In singleplayer (`Mode == Singleplayer`) `Host` is non-null (`PrepareHostIfNeeded` creates one for SP and MP-host), so the `Host == null` branch in `Broadcast` (`:185`) is a fallback for the **early-init / no-session** case. It calls `WindowManager.Shared.Present(alert)` directly — useful from menus or before any session exists. **In actual gameplay, even in SP, Broadcast goes through `SendToAll` which loops over `_clients` (containing just the host's `LocalGameClient`), envelopes through `SendToClients`, and round-trips via `LocalGameClient`'s one-frame queue.** So even SP broadcasts have a 1-frame delay before they appear locally.
2. **`Multiplayer.SendError` is `IPlayer`-typed** — works on `LocalPlayer` (host) or `RemotePlayer`. The `IsRemote` check decides between local-present (you can't `SendTo` yourself; the host is not in `_clients` as a remote-routable client) and `Host.SendTo`. **There is no `SendError(PlayerId, msg)` overload** — callers must resolve to `IPlayer` first via `PlayersManager.PlayerForId`.
3. **`AlertLevel` only affects `Toast` style.** `WindowManager.Present` reads `alert.Level` only in the `Toast` branch (`level == Error → ToastPosition.Middle, else Bottom`). The Console branch ignores `Level` entirely — chat alerts don't get color-coding from level. If you want red console text, you must embed TMP markup in the message string.

### `Multiplayer.Broadcast` callers (vanilla survey, ~40 sites)

The biggest emitters:

| Caller | What it announces |
|---|---|
| `DailyReportGenerator.GenerateReport` (`:167`) | "A new daily report is available." |
| `Progression.RequestStartPhase` (`Progression.cs:215, 222, 397`) | Phase paid / cars ordered / phase started |
| `LoanManager.HandleAdjustLoan` (`:187`) | Loan adjustment summary |
| `EquipmentPurchase.Apply` (`:33, 40, 53, 61`) | "ordered a shiny new {name}" / errors |
| `StateManager.ApplyToBalance` (`:1278`) | Payment receipts (host-side bookkeeping) |
| `Interchange.Apply` (`:153, 190`) | Interchange transfer / sale |
| `InterchangedIndustryLoader.Order` (`:126, 130`) | Cars ordered / insufficient funds |
| `PlayersManager.HandleSetTrainCrew*` (`:346, 356, 386, 398, 420, 424, 445, 448`) | Crew membership/timetable changes |
| `Industry.UpdateContract` (`:470`) | Contract started/closed |
| `OpsController.ProcessAreaSweep` (`:329`) and `:1083, 1087` | Sweep ops / passenger reschedule |
| `AutoEngineerPlanner.Say` (`:976`) | AI engineer speech (the only AI broadcaster) |
| `IndustryContext.Apply` (`:365`) | Per-industry text |
| `HostManager.AnnounceAccessChange` (`:1156`) | "X has banned/promoted Y" |
| `ScriptWorld.broadcast(message)` (`:58`) | Lua-driven broadcast — **client-bypass possible**; see [Scripting](scripting-moonsharp.md) |

### `Multiplayer.SendError` callers (~12 sites)

| Caller | What it errors |
|---|---|
| `FlareManager.Apply` (`:58`) | "Too close to switch." |
| `HostManager.SetAccessLevel` (`:1112`) | "Can't set host's access level." |
| `HostManager.RemovePlayerRecord` (`:1167`) | "Can't remove online player record." |
| `Progression.RequestStartPhase` (`:411, 424, 428`) | Internal error / DisplayMessage / "Unable to start phase" |
| `LoanManager.HandleAdjustLoan` (`:143, 147`) | DisplayMessage / "Unable to adjust loan." |
| `TrainController.HandleSetIdent` ValidationError lambda (`:2000`) | Reporting-mark / road-number validation |
| `AutoEngineerPlanner` (`:1567`) | Route error → `_routeRequester` |

The **`SendError` + `SendFireEvent(default(RequestRejected))` pair** appears in `TrainController.cs:2000-2001`: emit the per-player error toast AND broadcast a generic event so any UI can react. This is the canonical "request was rejected" pattern.

### `Network.Messages.Alert` wire format

```csharp
[MessagePackObject(false)]
public struct Alert(AlertStyle style, AlertLevel level, string message, double timestamp) : INetworkMessage
{
    [Key(0)] public AlertStyle Style    = style;
    [Key(1)] public AlertLevel Level    = level;
    [Key(2)] public string     Message  = message;
    [Key(3)] public double     Timestamp= timestamp;
}
```

Routing channel: `Channel.Message` (reliable; default for `INetworkMessage`s that aren't `SnapshotEnvelope`). See [Multiplayer Core › channel routing](multiplayer-core.md#channel-routing-multiplayerchannelformessage-networkmultiplayercs205).

The `Timestamp` field uses `TimeWeather.Now.TotalSeconds` (game time, not real time). Console renders timestamps via `new GameDateTime(alert.Timestamp)` — see `WindowManager.cs:99`. **Timestamps reflect host-side game-time at emit; clients render with their local clock formatting.** No drift correction.

### Patch candidates (Alert pipeline)

| Method | Why patch |
|---|---|
| `Multiplayer.Broadcast(string)` | Single chokepoint for system broadcasts. Prefix to filter/rewrite, postfix to log-tap (Discord webhook, accessibility filter, etc.). |
| `Multiplayer.SendError(IPlayer, string, AlertLevel)` | Single-target errors. Prefix to escalate (e.g., also `PostEphemeralLocal`) or log. |
| `WindowManager.Present(Alert)` | Receiver-side: re-route `Console`-styled alerts to a notice card, downgrade `Error` toasts to console lines, etc. **Patches here run on every machine that receives the alert.** |
| `HostManager.SendToAll`, `SendToAllExcept`, `SendTo` | Add custom routing (mod-only subset of clients). Cheap to patch — fan-out lives here. |
| Add a custom `INetworkMessage` Union member | Requires `[Union(N, typeof(YourMessage))]` injection; vanilla doesn't expose a hook (see [Multiplayer Core › Patch candidates](multiplayer-core.md#patch-candidates)). |

### MP authority

- `Multiplayer.Broadcast` and `Multiplayer.SendError` **dereference `Host`** (`Multiplayer.cs:191, 173`). On a non-host client, `Host` is `null` and **calling these would NRE** in the `else` branch (Broadcast) or `if (player.IsRemote)` branch (SendError) — but the `Host == null` short-circuit in Broadcast falls through to local Present. **`SendError` on a non-host client to a remote player NREs.** Mods must either gate by `IsHost` or fall back to `WindowManager.Shared.Present` themselves.
- `Network.Messages.Alert` is an `INetworkMessage`, not an `IGameMessage` — it has **no `[Authorization*Rule]` attribute** because it's host→client only. Clients never originate Alerts. There is no client→host "send me an alert" channel.

---

## Spine 2: Notice cards (`NoticeManager`)

```
HOST                                                CLIENT
─────                                                ──────
NoticeManager.Shared.PostEphemeral(entity, key, content)
   StateManager.AssertIsHost();                      ← throws on client
   StateManager.ApplyLocal(new PostNoticeEphemeral(  ← envelope as IGameMessage
       new SerializableEntityReference(entity), key, content));
       │
       └─→ Host.HandleGameMessage / SendToAll      ──► GameClient.HandleMessage
                                                      → StateManager.Handle / Apply
                                                        case PostNoticeEphemeral post:
                                                          NoticeManager.Shared.Handle(post)
                                                            entity = new EntityReference(post.Entity)
                                                            PostEphemeralLocal(entity, post.Key, post.Content)

PostEphemeralLocal(entity, contextualKey, content):
   if (entity.Type == EntityType.Player
       && entity.Id == PlayersManager.PlayerId.ToString())
       return;                                         ← self-suppression
   key = $"{(int)entity.Type}//{entity.Id}//{contextualKey}";
   if (_notices.TryGetValue(key, out e)) {
       if (string.IsNullOrEmpty(content)) { DismissRow(key); return; }   ← empty content = clear
       if (e.Content == content) return;                                   ← idempotent
       DismissRow(key);                                                    ← else replace
   } else if (string.IsNullOrEmpty(content)) {
       return;                                                             ← clearing nothing = noop
   }
   ScheduledAudioPlayer.PlaySoundLocal("telegraph-ditdit");
   row = Instantiate(rowTemplate, rowContainer);
   row.label.text = LabelTextForNotice(entity, content);                   ← TMP markup with Hyperlink prefix
   row.OnDismiss = () => DismissRow(key);
   row.SetOffscreen(true, false); row.SetOffscreen(false, true);           ← slide-in animation
```

### Wire format

```csharp
[HostOnlyAuthorizationRule]
[MessagePackObject(false)]
public struct PostNoticeEphemeral(SerializableEntityReference entity, string key, string content) : IGameMessage
{
    [Key(0)] public SerializableEntityReference Entity  = entity;
    [Key(1)] public string                       Key    = key;
    [Key(2)] public string                       Content= content;
}
```

`HostOnly` enforced by `HostOnlyAuthorizationRuleAttribute` — clients sending this message would be rejected by `HostManager.CheckAuthorizedToSendMessage`. The host calls `StateManager.ApplyLocal(new PostNoticeEphemeral(...))` after an `AssertIsHost()` precheck (`NoticeManager.cs:50`).

### Label format and hyperlink integration

```csharp
private string LabelTextForNotice(EntityReference entity, string content)
{
    string text = Hyperlink.To(entity).ToString();
    return "<style=b>" + text + "</style>  <style=p>" + content + "</style>";
}
```

- **Entity prefix is always a hyperlink.** Built via `Hyperlink.To(EntityReference)` (`Hyperlink.cs:62`) which resolves through `EntityReference.Text()`. For `Player` entities, the prefix is the player's name; for `Car`, the car's `DisplayName`; etc.
- **Content is raw TMP markup**, no `<noparse>` wrapping. If you ship user-generated text (player name, free-form input), wrap it: `content.ConsoleEscape()` (`Console.cs:14`). Vanilla never does this — all content is hard-coded strings.
- **Body text is left of the entity prefix in markup but right visually** (the label is a single TMP_Text; layout is inline). Two `<style>` spans (`b` for bold, `p` for paragraph) — both styles must exist in the project's TMP style sheet.
- **Notice card is clickable** because `NoticeRow.label` (TMP_Text on the prefab) has `TextLinkReceiver` baked into the asset. Confirmed empirically; not visible in the decompile because Unity-asset bindings don't show in C#. The receiver routes to `LinkDispatcher.Open` via the default path (no `OnLinkClicked` override). Clicking the bold-prefix entity link opens the relevant window.

### `PostEphemeralLocal` self-suppression — NOT a bug

The hyperlink-entityref crib (`hyperlink-entityref.md` "Gotchas") flagged the self-suppression check as suspect:
```csharp
if (entity.Type == EntityType.Player && entity.Id == PlayersManager.PlayerId.ToString()) return;
```
suggesting it should compare against `.String` instead. **`PlayerId.ToString()` is overridden** (`PlayerId.cs:23`) to return `_playerId` — the same value as `.String` (`:9`). **The two are interchangeable, the check is correct.** Cross-link: cite this resolution back from `hyperlink-entityref.md` if the gotcha section is ever revised.

The **actual subtlety** is that self-suppression is *only* applied to `Player`-typed entities. For `Car`-keyed notices (the most common), notices fire on the originator's screen too. `AutoEngineerFuelAlerter` PostNotice for fuel low — the player driving sees it just like everyone else.

### Coalescing key

`$"{(int)entity.Type}//{entity.Id}//{contextualKey}"` — three-part composite. Two notices on the *same* car with *different* `contextualKey`s are independent rows. Same car + same key = idempotent (re-post with identical content is a no-op; re-post with new content replaces). **Empty/null content dismisses.** This is the *only* dismiss API exposed publicly — there's no `Dismiss(entity, key)` method on `NoticeManager`; `DismissRow(key)` is private and only the on-row dismiss button + the empty-content path can trigger it externally.

### Audio

`ScheduledAudioPlayer.PlaySoundLocal("telegraph-ditdit")` fires *only* on fresh post (not on idempotent re-post, not on dismiss). Local-only — each receiving client plays it independently. Multiple notices in the same frame overlap.

### Container layout

`NoticeRow` is instantiated as a child of `rowContainer` (assigned in inspector). New rows append at the bottom (Unity child order). Each row animates with `LeanTween` 0.25s slide-in from `+300px` X to 0. Dismiss animation is 0.25s slide-out + 0.125s pause + Destroy. **There is no max-row cap, no pagination, no scroll wrapper visible in the decompile.** Heavy notice flooding will accumulate rows indefinitely.

### Vanilla notice emitters

| Caller | Pattern |
|---|---|
| `Game.Notices.NoticeExtensions.PostNotice(this Car car, key, content)` | The only typed extension; wraps `PostEphemeral(new EntityReference(EntityType.Car, car.id), key, content)` |
| `PlayersManager.NotifyOfConnected` (`:260`) | `PostEphemeralLocal(new EntityReference(playerId), "conn", "Connected")` — local-only, fires on roster diff |
| `PlayersManager.NotifyOfDisconnected` (`:276`) | Same pattern, "Disconnected" content |
| `AutoEngineer.HandleStop` (`:1124`) | `Locomotive.PostNotice("ai-stop", message)` |
| `AutoEngineerFuelAlerter` (`:48, 49, 103, 143, 158`) | Fuel/water alerts; key is per-resource; null content clears |
| `AutoEngineerPassengerStopper` (`:337`) | "Timetable schedule complete." |
| `AutoEngineerPlanner` (`:1113, 1869`) | AI pitfall + waypoint notices |

**Notable absences:** No vanilla code posts notices on `Industry`, `PassengerStop`, `Crew`, or `Position` entity types. The `Player` type is used only by `PlayersManager` for connect/disconnect (which goes through `PostEphemeralLocal` directly — no host fan-out). All `Car` notices come from AI subsystems on the locomotive. **No notice ever uses `Hyperlink.To(EntityReference).Text()` resolved to "Unknown"** in vanilla — but a mod posting on `Crew`/`Help`/`Position` would render with a `"Unknown"` bold prefix because `EntityReference.Text()` has no case for those.

### Patch candidates (NoticeManager)

| Method | Why patch |
|---|---|
| `NoticeManager.PostEphemeralLocal` | The actual UI mutator. Prefix to silence specific keys, postfix to mirror to an external log/Discord. **The single fix point for self-suppression behavior** (e.g., to extend it to non-Player entities). |
| `NoticeManager.Handle(PostNoticeEphemeral)` | Network receive intercept. Less useful than `PostEphemeralLocal` (which is also called by local-only paths); patch this only if you specifically need to filter network notices. |
| `NoticeManager.LabelTextForNotice` | Customize the bold-prefix style or replace `Hyperlink.To` with a different label resolver. |
| `NoticeManager.PostEphemeral` | Host-emit. Prefix to gate; doesn't bypass `AssertIsHost()`. |
| `NoticeExtensions.PostNotice(this Car, ...)` | Add typed wrappers for other entity types (currently only `Car` has a convenience extension). |

### MP authority

- `PostNoticeEphemeral` IGameMessage: `[HostOnlyAuthorizationRule]`. Client sends are rejected (returns false from `CheckAuthorizedToSendMessage`).
- `PostEphemeral` (the host emit): asserts host via `StateManager.AssertIsHost()` — throws on client.
- `PostEphemeralLocal` (local mutator): **no auth gate**. Anyone can call it locally to display a notice on their own machine. This is the documented mod path for client-only notices.
- `Handle(PostNoticeEphemeral)`: invoked by the StateManager dispatch (`StateManager.cs:629`) on every machine that receives the message. Not directly callable from a client without the host having fired the message.

### Gotchas (NoticeManager)

- **Singleton via `FindObjectOfType<NoticeManager>()`.** First call is slow; cached after. If the scene doesn't contain a `NoticeManager`, `Shared` returns `null` and `NoticeExtensions.PostNotice` warns rather than throws. Direct callers (`PlayersManager`, AI modules) NRE.
- **Empty content is the only clear path.** `PostNotice("foo", null)` and `PostNotice("foo", "")` both dismiss row "foo". Distinguishing "clear" from "empty post" requires checking `string.IsNullOrEmpty(content)` upstream.
- **Client-side `PostEphemeral` calls would assert-throw**, not silently fail. Mods that want a "post on host, fall back to local on client" pattern must check `StateManager.IsHost` themselves and route through `PostEphemeralLocal` on the client.
- **Self-suppression is `Player`-only.** A `Car`-typed notice posted by car-id `LV-1234` does not suppress on the player driving that car. Notice flooding on cars driven by the originator is the most common UX pain point.
- **Audio is hard-coded `"telegraph-ditdit"`.** No way to suppress per-call; patch `PostEphemeralLocal` or `ScheduledAudioPlayer.PlaySoundLocal` to silence.
- **No `Dismiss(entity, key)` API.** To clear a notice without an entity reference, you must reconstruct the same `EntityReference` and call `PostEphemeralLocal(entity, key, null)`. Mods can't enumerate active notices (the `_notices` dictionary is private).
- **`rowContainer` has no pruning.** A bug or runaway script can fill the screen with notice rows. Manually call `NoticeManager.Shared.Clear()` to wipe (it's `public`, no auth gate).
- **`Clear()` is brutal.** Wipes `_notices` dict and destroys all children except `rowTemplate`. No animation, no per-row dismiss callback. Use sparingly.
- **`PostEphemeralLocal` calls `EntityReference.Text()` indirectly via `LabelTextForNotice` → `Hyperlink.To(EntityReference)`.** If `Text()` returns `"Unknown"` for Crew/Position/Help (per [Hyperlink crib](hyperlink-entityref.md#entityreference--the-uri-codec)), the notice will visually say "Unknown content" — and clicking still routes to the entity (via the URI in the `<link>` attribute). Mod-add `EntityReference.Text()` cases when posting notices on those types.

---

## Spine 3: Modal alerts (`ModalAlertController`)

```
ModalAlertController.PresentOkay("Title", "Body")          ← shorthand for one-button
   │  → Present<int>(title, msg, [(0,"Okay")], _ => onOkay?.Invoke())
   │
ModalAlertController.Present<T>(title, msg, buttons, onButton)
   │  → Shared._Run<T>(title, msg, /*inputString*/ null, buttons, tup => onButton(tup.Item1))
   │
ModalAlertController.Present<T>(title, msg, inputStr, buttons, onButton)  ← input variant
   │  → Shared._Run<T>(title, msg, inputStr, buttons, onButton)
   │
ModalAlertController.Present(Action<UIPanelBuilder, Action> closure, int width=400)  ← free-form
   │  → Shared._Run(closure, width)

_Run(closure, width=400):
   ModalAlert m = Instantiate(alertPrefab, canvas.transform);
   m.gameObject.SetActive(true);
   m.GetComponent<RectTransform>().SetFrameFillParent();
   m.Configure(closure, width);                        ← LeanTween scale-in 0.25s
   // ActivateInputField on first active TMP_InputField (auto-focus)

ModalAlert.Configure(closure, width):
   alertRectTransform.sizeDelta.x = width;
   _panel = UIPanel.Create(contentRectTransform, builderAssets, builder => closure(builder, Dismiss));
   Present();                                          ← scale-in animation

ModalAlert.Dismiss():
   LeanTween fade 0.125s + delayedCall 0.25s → _panel.Dispose(); Destroy(gameObject);
```

### Three entry points

| Entry point | Signature | Use case |
|---|---|---|
| `PresentOkay(title, msg, onOkay=null)` | `static void` | Single-button information modal. `onOkay` fires after dismiss. |
| `Present<T>(title, msg, IEnumerable<(T, string)> buttons, Action<T> onButton)` | `static void` | Multi-button choice. `onButton` receives the value tuple's first element. |
| `Present<T>(title, msg, string inputString, IEnumerable<(T, string)> buttons, Action<(T, string)> onButton)` | `static void` | Multi-button + text input field. `onButton` receives `(buttonValue, currentInputText)`. |
| `Present(Action<UIPanelBuilder, Action> closure, int width=400)` | `static void` | Free-form panel; `closure` receives the builder and a `dismiss` action. Used by complex prompts (CrewsPanelBuilder, TimetableLoadSaveHelper). |

### Layout (built by `_Run<T>`)

The typed `_Run` (`ModalAlertController.cs:48`) hands the closure-form `_Run` a builder closure that:
1. Adds the title (`fontSize=22`, center-aligned).
2. Adds the message (`fontSize=18`, center) **only if non-empty** — `null`/`""` skips the body line.
3. Adds an input field if `inputString != null`. **`null` and `""` are different here** — pass `""` to get an empty input field, `null` to skip the field entirely.
4. Adds an `AlertButtons(...)` row that iterates the `buttons` enumeration.

### `AlertButtons` (`UI.Builder/UIPanelBuilder.cs:504`)

Per-platform button ordering quirk:

```csharp
bool cancelLast = Application.platform switch {
    RuntimePlatform.WindowsPlayer => true,
    RuntimePlatform.WindowsEditor => true,
    _ => false,
};
```

If a button's text matches the literal string `"Cancel"`, it's reordered to be **last on Windows, first on macOS/Linux** (matches platform UX conventions). **The match is case-sensitive on the literal string `"Cancel"`** (`UIPanelBuilder.cs:524`). A button labeled `"Cancel."` or `"cancel"` won't be reordered. **Patch surface for localization:** if your mod ships a localized "Cancel" button, the reorder logic won't recognize it.

### Button click → callback → dismiss

```csharp
uIPanelBuilder.AddButtonMedium(buttonText, delegate
{
    try
    {
        onButton((buttonValue, _inputString));
    }
    catch (Exception exception)
    {
        Log.Error(exception, "Error in action for {button}", buttonText);
        return;                                   ← exception in callback aborts the dismiss
    }
    dismiss?.Invoke();
});
```

**Critical:** if `onButton` throws, the modal does **not** dismiss. The user is stuck unless they click another button. There's no "X" close button visible in the decompile — modal dismissal is button-only (or `Configure`'s caller-driven via `dismiss` closure).

The callback-then-dismiss order means: **if your `onButton` opens a new modal, it stacks on top before the current one dismisses.** Both are visible briefly during the 0.125s fade.

### Free-form `Present(Action<UIPanelBuilder, Action>)`

The closure receives `(UIPanelBuilder builder, Action dismiss)`. **You are responsible for calling `dismiss()`** — otherwise the modal stays up forever. Typical pattern:

```csharp
ModalAlertController.Present(delegate(UIPanelBuilder builder, Action dismiss)
{
    builder.AddLabel("Custom modal");
    builder.AddButton("Close", () => { /* do thing */; dismiss(); });
});
```

The `width` parameter (default `400`) sets `alertRectTransform.sizeDelta.x`. Height is content-driven via the panel builder.

### **No queue, no stack, no dismiss-by-id**

Each `Present` call instantiates a new `ModalAlert` MonoBehaviour as a child of `Shared.canvas.transform`. Multiple modals coexist as separate children. Their input-field auto-focus iterates all active TMP_InputFields and focuses the first — meaning if you stack two input modals, only the first's field is auto-focused.

There is **no `IsAnyModalOpen()` API**, no list of active modals, no way to dismiss programmatically without holding a reference (which `Present` doesn't return). Mods needing modal-state introspection must either patch `_Run` to track instances or `FindObjectsOfType<ModalAlert>()`.

**Modal cancel by escape key, click outside, or closing the window is NOT visible in the decompile.** The `ModalAlert` MonoBehaviour exposes only `Configure`, `Present` (private), `Dismiss` (private). The asset prefab may have a backdrop button wired to `Dismiss` via Unity Events — confirm in-engine.

### Vanilla modal callers (full survey)

| Site | Type | Purpose |
|---|---|---|
| `SaveManager.Load` (`:62`) | PresentOkay | "Error loading save" |
| `SaveManager.Save` (`:80`) | PresentOkay | "Error saving game" |
| `StateManager.ReturnToMainMenuWithError` (`:1344`) | PresentOkay | Generic load/error wrapper |
| `OpsController.Awake` (`:159`) | PresentOkay | "No Interchanges Enabled" |
| `ClientManager.ClientDidDisconnect` (`:170`) | PresentOkay | "Disconnected" + reason |
| `ClientManager.ClientDidReceivePasswordPrompt` (`:261`) | Present (input) | "Password Required" |
| `PauseMenu.QuitGame` (`:133`) | Present (3-button) | Discard/Save/Cancel |
| `PreferencesMenu` (`:29, 39`) | Present + PresentOkay | Reset confirm + post-reset notice |
| `LoadGameMenu` (`:77`) | Present (2-button) | Delete save confirm |
| `SettingsPanelBuilder.RequestSaveReopen` (`:247`) | PresentOkay | "Reload Required" — fires on `OilFeature` toggle |
| `CrewsPanelBuilder.DeleteTrainCrew` (`:260`) | Present (2-button) | Delete crew confirm |
| `CrewsPanelBuilder.NewCrew` (`:283`) | Present (free-form) | Custom input UI |
| `LostCarPlacerWindow.OnDismiss` (`:122`) | Present | Close-without-placing confirm |
| `CTCPanelMarker.EditMarker` (`:175`) | Present (input + 3-button) | Edit marker text |
| `InteractiveBookWindow.OpenBook` (`:118`) | PresentOkay | "Error opening {book}" |
| `InteractiveBookWindow.CloseBook` (`:204`) | Present | Confirm close |
| `TimetableEditorWindow` (`:105`) | Present | Conflict resolution |
| `TimetableLoadSaveHelper` (`:48, 163, 185`) | Present + PresentOkay | Save/load timetable |
| `VisualTimetableEditor` (`:489, 509, 520, 561, 653, 681, 699`) | Present + PresentOkay | Train/symbol management |
| `TutorialManager.Awake` (`:57`) | PresentOkay | **"The tutorial has changed!"** — the legacy save-migration modal |

### The `<saveformat-migration>` modal pattern

Per `tutorial.md`'s reference, the legacy-save migration modal is `TutorialManager.cs:57`:

```csharp
ModalAlertController.PresentOkay(
    "The tutorial has changed!",
    "It looks like this game was started with the original tutorial. If you wish to continue with the tutorial we recommend starting a new Company mode game. We apologize for the inconvenience!\n\nIn Railroader 2025.1 the tutorial was revamped and is not compatible with the original one.");
```

This is the entire "save-format migration" UX surface in vanilla — a single hardcoded `PresentOkay` call inside `TutorialManager.Awake`, fired when the loaded save's `tutorial` KVO contains the legacy `stack` key (the old tutorial format) instead of the new `chapter_id`/`page_id` keys. **There is no migration framework — no `IMigrationModal`, no version-dispatch table, no opt-in/opt-out.** The single-string body is the entire migration story for tutorial saves.

For other migration scenarios (settings, preferences, etc.), the pattern is identical: detect-on-load → `PresentOkay`. There's no "migration center," no "view all migrations" UI, no reapply mechanism.

### Save-error modal flow

```
SaveManager.Save(saveName, saveTag):
   StateManager.DebugAssertIsHost();                 ← assertion is empty in vanilla, no-op
   try { WorldStore.Save(saveName); }
   catch (Exception ex) {
       Debug.LogException(ex);
       Log.Error(ex, "Error saving to {saveName}", saveName);
       ModalAlertController.PresentOkay("Error saving game", "An error occurred while saving to " + saveName);
   }
   RestartAutosave();                                ← runs even after save failure
```

Same pattern for `Load` (`:62`) — `PresentOkay`, no retry, no diff. The exception detail is **not** included in the modal body — only the save name. Logged via `Serilog`. Mods that need richer save-error UI must patch `SaveManager.Save`/`Load` or replace the modal.

**`StateManager.ReturnToMainMenuWithError(title, message)`** (`StateManager.cs:1341`) is the "go to main menu and show modal" helper. Used by `ClientManager.ClientDidReceiveSnapshot` failure paths (`:240, 244`) and could be used by mods needing a hard-fail UX.

### Patch candidates (Modal)

| Method | Why patch |
|---|---|
| `ModalAlertController._Run<T>` (private) | Intercept all typed `Present*` calls. Add titlebar buttons, custom default buttons, etc. |
| `ModalAlertController._Run(Action<UIPanelBuilder,Action>)` (private) | Intercept all free-form modals — add a global "minimize" or "X" close. |
| `ModalAlertController.Present*` (static) | Wrapper-level patches. Useful for centralized logging or telemetry of modal usage. |
| `UIPanelBuilder.AlertButtons` | Customize the Cancel-reorder logic, button spacing, etc. **Affects every modal.** |
| `ModalAlert.Dismiss` | Add a callback hook on dismissal for stacking-aware mods. |

To **add a custom modal type** (e.g., a queue-aware "non-blocking modal"), you must instantiate your own `ModalAlert` clone or build a parallel system — `ModalAlertController` doesn't expose a hook. Cleanest path: ship a `ModModalController` with the same API plus your additions; the singleton field is `private`, so you don't conflict.

### MP authority (Modal)

- **None.** All modal presentation is local-only. There is no replicated modal, no "show this dialog on every client" message. If your mod needs a host-driven dialog on a client, route through:
  - `Multiplayer.SendError(player, msg)` for one-line toasts
  - `Multiplayer.Broadcast(msg)` for chat-line notifications
  - `NoticeManager.PostEphemeral(...)` for entity-keyed cards
  - Or define your own `IGameMessage` with a client-side handler that calls `ModalAlertController.PresentOkay`.

### Gotchas (Modal)

- **Singleton via `Shared = this` in `Awake`** (`ModalAlertController.cs:24`). If two `ModalAlertController` instances exist (mod-injected scene), the second wins. Static `Present*` calls throw `Exception("No ModalAlertController")` if `Shared == null` — so pre-load static calls fail loudly.
- **`alertPrefab` is `[SerializeField]`** — mod that wants to replace the modal asset must replace the prefab reference on the singleton. No code-side override.
- **Auto-focus iterates `GetComponentsInChildren<TMP_InputField>()`.** If you stack two input modals, the second's field doesn't get focus because the iteration runs once per `_Run`. Inputs in the second modal must be clicked.
- **Exception in `onButton` blocks dismiss.** If you do work in the callback, wrap in your own try/catch to ensure dismiss runs.
- **No way to programmatically dismiss without a button click** — the `dismiss` Action is captured inside the closure and never exposed. Free-form `Present(closure)` is the only way to keep a `dismiss` reference, and only inside the closure scope.
- **Multi-modal stacking has no z-order management.** Newer modals are later child indices, so they render on top by Unity's overlay rules. But input-blocking is per-modal — clicking on an older underlying modal's button still works (as long as the newer modal doesn't physically cover it).
- **Cancel-reorder is platform-conditional and substring-fragile.** A button labeled `"Cancel "` (trailing space), `"cancel"`, or `"Annuler"` (localized) will not be detected.
- **No `OnModalShown` / `OnModalDismissed` events.** Consumers cannot react to the modal lifecycle without patching.
- **`ModalAlert.Dismiss` is private** — you can't call it from outside. The closure-form `dismiss` callback (passed to free-form `Present`) is the only public entry point to dismissal.
- **The `[ContextMenu("Demo Alert")] PresentDemo()` method** (`:106`) is editor-only debug. Calls `Toast.Present` from the modal callback — so demo dismisses the modal AND fires a toast.

---

## Spine 4: `Toast` (singleton ephemeral text)

```csharp
public class Toast : MonoBehaviour {
    public RectTransform rectTransform;
    public CanvasGroup   canvasGroup;
    public TextMeshProUGUI text;
    private static Toast _instance;

    public static void Present(string text, ToastPosition position = ToastPosition.Middle) {
        if (_instance == null) _instance = FindObjectOfType<Toast>();
        if (_instance == null) Debug.LogError("Couldn't find Toast instance in scene.");
        else _instance.Run(text, position);
    }
}
```

### Animation

`Run`:
1. Cancel any in-flight LeanTween on rectTransform/canvasGroup.
2. Position vertically by `ToastPosition`: `Middle` → 0.5 lerp, `Bottom` → 0.0 lerp, of `(parentRectHeight - 100)`.
3. Scale: 0.75 → 1.0 (0.5s, ease-out-elastic) → wait 1.9s → 0.5 (0.5s ease-in-cubic).
4. Alpha: 0 → 1 (0.25s) → wait 2.25s → 0 (0.25s).

**Total visible time: ~2.5–2.7 seconds** depending on tweens. Not configurable per call. Re-presenting (calling `Toast.Present` again before fade-out completes) **cancels the in-flight tween** and starts a fresh animation — so spammy toasts don't queue, the latest wins.

### `ToastPosition`

```csharp
public enum ToastPosition { Middle, Bottom }
```

`Middle` = 0.5 lerp (centered vertically, minus 100px), `Bottom` = 0.0 lerp (near bottom). **`WindowManager.Present(Alert)` maps `AlertLevel.Error → Middle, else Bottom`** (`WindowManager.cs:96`). Direct `Toast.Present(msg)` defaults to `Middle`.

### Direct `Toast.Present` callers

These bypass `Multiplayer.SendError` entirely (purely local toasts):

| Caller | Reason |
|---|---|
| `LinkDispatcher.Open` Car-not-found path (`:62`) | "Car not found." (verified via [Hyperlink crib](hyperlink-entityref.md)) |
| `ModalAlertController.PresentDemo` (`:115`) | Editor demo |

Most "toast"-like UX in vanilla actually goes through `Multiplayer.SendError` or `NoticeManager` — direct `Toast.Present` is rare.

### **Toast hyperlink non-clickability — confirmed**

`Toast.cs:13` — `public TextMeshProUGUI text;`. **No `TextLinkReceiver` field, no `AddComponent` in `Awake`, no asset binding visible in the source.** Per `hyperlink-entityref.md`, this means hyperlinks render visually (TMP renders `<link>` tags as styled text) but **are not clickable** — `OnPointerClick` doesn't fire because no receiver exists.

To fix: postfix `Toast.Awake` with `text.gameObject.AddComponent<TextLinkReceiver>()`. This adds receiver behavior to the singleton's TMP_Text once on load. Alternatively, add the component to the Toast prefab in Unity.

### Patch candidates (Toast)

| Method | Why patch |
|---|---|
| `Toast.Run` | Customize duration, position, animation — the whole appearance pipeline. |
| `Toast.Present` | Add gating (e.g., suppress duplicate toasts within N seconds), routing to alternative surfaces. |
| `Toast.Awake` | Add `TextLinkReceiver` to make hyperlinks clickable. |

### MP authority (Toast)

- **None.** Local-only display surface. Network-driven toasts arrive as `Alert(AlertStyle.Toast, ...)` and route through `WindowManager.Present`.

### Gotchas (Toast)

- **Singleton via `FindObjectOfType<Toast>()`** — same caveat as `NoticeManager`: missing scene component → `Debug.LogError` rather than throw. Calls become silent no-ops.
- **No queue, no stack.** A new `Present` interrupts the previous toast — you cannot show two toasts simultaneously. For multi-line UX, embed line breaks in the message.
- **`TextMeshProUGUI text` is a public field** — direct mutation works. Mods can postfix `Run` to e.g. swap the font/color per call.
- **Position is fixed at the start of animation.** Resizing the parent canvas mid-animation does not reposition.
- **Hyperlinks render but don't click** — the headline gotcha.

---

## Spine 5: Console alerts (the chat surface)

`AlertStyle.Console` alerts route through `Console.shared.AddLine(message, new GameDateTime(timestamp))` (`WindowManager.cs:99`). The `Console` MonoBehaviour adds an `Entry` to the expanded view (history) and the collapsed view (HUD ribbon).

### `AddLine` signature

```csharp
public void AddLine(string text)                                  // → AddLine(text, TimeWeather.Now)
public void AddLine(string text, GameDateTime gameDateTime)
```

`Console.Log(string)` (the static helper in `Console.cs`) wraps `AddLine(text)` and **stamps the local `TimeWeather.Now`** — not the host's emit time. So local logs interleave by their own time vs host-emitted alerts that carry an explicit timestamp.

### Console-line click-through

The console's TMP prefab (managed by `ConsoleLinePool`) **has `TextLinkReceiver` baked into the asset**. Hyperlinks in alert messages are clickable — the click routes to the default `LinkDispatcher.Open` path (no `OnLinkClicked` override). Cross-link to [hyperlink crib § Where receivers come from](hyperlink-entityref.md#where-receivers-come-from).

### `Console.Log` vs `Multiplayer.Broadcast`

| Path | Effect |
|---|---|
| `Console.Log("foo")` | Local-only line, stamped with `TimeWeather.Now` |
| `Multiplayer.Broadcast("foo")` | Host emits `Alert(Console, Info, ...)` to all; each receiver renders via their `WindowManager.Present` → `Console.AddLine` |

Mods must distinguish: `Console.Log` for "show on my screen only" vs `Multiplayer.Broadcast` for "show on all screens." There's no client-originated chat broadcast — clients use `Game.Messages.Say` (a separate `IGameMessage` with `Passenger`-min auth) which the host wraps and re-broadcasts via the same Alert path. See [Hyperlink crib § Chat & broadcast pipeline](hyperlink-entityref.md#chat--broadcast-pipeline-where-hyperlinks-live-in-mp-messages).

---

## Cross-cutting: `Game.Events.RequestRejected`

```csharp
[StructLayout(LayoutKind.Sequential, Size = 1)]
public struct RequestRejected { }
```

Empty `Messenger` struct — **payload-free signal**. Dispatched via `StateManager.SendFireEvent(default(RequestRejected))` which routes through `FireEvent` code 2 (`StateManager.cs:1003`) and on receive triggers `Messenger.Default.Send(default(RequestRejected))` on every machine.

### **Correction to prior crib (`access-control.md`):** `RequestRejected` is NOT dead code

The access-control crib called `RequestRejected` "vestigial / never invoked." This is **incorrect** — `TrainController.HandleRequestSetIdent` (the host-side ident validator) emits `RequestRejected` whenever `CarSetIdent` validation fails:

```csharp
void ValidationError(string message)
{
    Multiplayer.SendError(sender, message);
    StateManager.Shared.SendFireEvent(default(RequestRejected));
}
```
(`TrainController.cs:1998-2002`)

The pattern is: **emit a per-player `SendError` toast AND broadcast a global `RequestRejected` event** so any UI can react (typically by refreshing to show the unchanged value).

The **only consumer** in vanilla is `CarCustomizeWindow.RebuildUponRequestReject` (`:152`):

```csharp
private void RebuildUponRequestReject() {
    Messenger.Default.Register<RequestRejected>(this, delegate {
        _panel.Rebuild();
        Unregister();
    });
    LeanTween.delayedCall(1f, Unregister);
    void Unregister() { Messenger.Default.Unregister<RequestRejected>(this); }
}
```

The window registers a one-shot listener with a 1-second auto-unregister timeout, then sends the `RequestCarSetIdent` request. If host rejects, the rebuild fires; if not, the timeout cleans up the registration.

**Mod implication:** any system implementing optimistic-UI patterns (apply locally, request from host, revert on rejection) can subscribe to `RequestRejected` for cheap rollback signaling. **But there's no payload — you can't tell *which* request was rejected.** A mod that registers globally and rebuilds on every reject will spam unnecessary work. The 1-second auto-unregister is the canonical mitigation.

### Patch candidates (RequestRejected)

| Hook | Why |
|---|---|
| `StateManager.SendFireEvent` (host-side) | Patch to log/inspect what's being rejected. |
| `StateManager.HandleFireEvent` case 2 (`:1003`) | Receive-side; pre-rebroadcast, you could enrich with a payload via a side-channel. |
| `Messenger.Default.Register<RequestRejected>` | Subscribe in your mod. |

---

## Cross-cutting: where these surfaces converge

```
                            ┌─────────────────────────────────┐
                            │  Multiplayer.Broadcast(msg)     │  Multiplayer.SendError(p, msg)
                            │      → Host.SendToAll(alert)    │      → Host.SendTo(p, alert)
                            │           or Present locally    │           or Present locally
                            └────────────────┬────────────────┘
                                             │ Alert (INetworkMessage)
                                             ▼
                              GameClient.HandleMessage → ClientDidReceiveAlert
                                             │
                                             ▼
                                  WindowManager.Present(Alert)
                                       /          \
                                Toast.Present     Console.shared.AddLine
                              (singleton TMP,    (line pool prefab w/
                              NO TextLinkReceiver) TextLinkReceiver baked in)

NoticeManager.PostEphemeral(entity,key,content)         ModalAlertController.Present*(...)
   → ApplyLocal(PostNoticeEphemeral msg) [HostOnly]        → local-only
   → host fan-out, all clients call NoticeManager.Handle   → singleton, multi-instance child modals
                  → PostEphemeralLocal                     → no replication
                     → Instantiate NoticeRow (TMP w/        → free-form UIPanelBuilder closure
                        receiver per asset)                   variant for complex prompts
```

### Surface decision matrix

| Want… | Use |
|---|---|
| One-line ephemeral, all players see, log to chat history | `Multiplayer.Broadcast(msg)` |
| One-line ephemeral, single player, error styling | `Multiplayer.SendError(player, msg)` (defaults to `AlertLevel.Error` → centered toast) |
| One-line ephemeral, single player, info styling | `Multiplayer.SendError(player, msg, AlertLevel.Info)` (bottom toast, no error styling) |
| Persistent card tied to a car/player/industry/etc. | `NoticeManager.Shared.PostEphemeral(entityRef, key, content)` (host-emit, all see) |
| Persistent card local-only | `NoticeManager.Shared.PostEphemeralLocal(entityRef, key, content)` |
| Blocking dialog with buttons | `ModalAlertController.Present*` |
| Blocking dialog, single OK | `ModalAlertController.PresentOkay` |
| Error popup local-only | `ModalAlertController.PresentOkay("Error", msg)` |
| Local toast no replication | `Toast.Present(msg, position)` |
| Local console line, no replication | `Console.Log(msg)` |
| Reactive UI on rejected request | `Messenger.Default.Register<RequestRejected>` + 1s auto-unregister |

---

## Patch surface summary (one table)

| Goal | Hook | File:line |
|---|---|---|
| Intercept all system broadcasts | `Multiplayer.Broadcast(string)` Prefix/Postfix | `Network/Multiplayer.cs:181` |
| Intercept all single-target errors | `Multiplayer.SendError(IPlayer, string, AlertLevel)` | `Network/Multiplayer.cs:167` |
| Intercept all incoming Alerts (receiver-side) | `WindowManager.Present(Alert)` | `UI.Common/WindowManager.cs:91` |
| Re-route Toast to Console | Patch the `case AlertStyle.Toast` arm in `WindowManager.Present` | `UI.Common/WindowManager.cs:95` |
| Make toasts clickable | Postfix `Toast.Awake` to `AddComponent<TextLinkReceiver>()` on `text` | `UI.Common/Toast.cs:35` |
| Tap notice card lifecycle | `NoticeManager.PostEphemeralLocal` Prefix/Postfix | `Game.Notices/NoticeManager.cs:66` |
| Filter notices by entity type | `NoticeManager.Handle(PostNoticeEphemeral)` Prefix | `Game.Notices/NoticeManager.cs:60` |
| Suppress notice audio | Patch `ScheduledAudioPlayer.PlaySoundLocal` callsite or `PostEphemeralLocal` | `Game.Notices/NoticeManager.cs:93` |
| Customize notice label | `NoticeManager.LabelTextForNotice` | `Game.Notices/NoticeManager.cs:109` |
| Add typed notice extensions | New static class with `this Industry`/`this PassengerStop`/etc. | parallel to `Game.Notices/NoticeExtensions.cs` |
| Intercept all modal presentations | `ModalAlertController._Run<T>` and `_Run(closure, width)` | `UI.Common/ModalAlertController.cs:48, 128` |
| Track modal open/close lifecycle | Patch `ModalAlert.Configure` (open) + `Dismiss` (close) | `UI.Common/ModalAlert.cs:34, 49` |
| Customize Cancel-button reorder | `UIPanelBuilder.AlertButtons` | `UI.Builder/UIPanelBuilder.cs:504` |
| Add modal queue / max-stack | Wrap `ModalAlertController._Run` in a queue gate | `UI.Common/ModalAlertController.cs:48` |
| Save-error modal upgrade | `SaveManager.Save`/`Load` Prefix | `Game.State/SaveManager.cs:51, 68` |
| React to RequestRejected globally | `Messenger.Default.Register<RequestRejected>` | (subscribe; no patch) |
| Inspect what's being rejected | `StateManager.SendFireEvent` Prefix on `evt is RequestRejected` | `Game.State/StateManager.cs:952` |

---

## MP authority summary

| Surface | Auth | Notes |
|---|---|---|
| `Multiplayer.Broadcast` | Effectively HostOnly | Uses `Host.SendToAll` which only the host has populated. Client call would NRE on `Host.SendToAll` (Host is null on client). |
| `Multiplayer.SendError` | Effectively HostOnly for remote targets | `Host.SendTo` requires host. Local target works on any machine. |
| `Network.Messages.Alert` | Host → client only | INetworkMessage union; clients never originate Alerts. No request/auth attribute. |
| `PostNoticeEphemeral` IGameMessage | `[HostOnlyAuthorizationRule]` | Client sends are silently rejected by `CheckAuthorizedToSendMessage`. |
| `NoticeManager.PostEphemeral` | Asserts host (`AssertIsHost()`) | Throws on client. |
| `NoticeManager.PostEphemeralLocal` | None | Anyone can call locally; documented mod path for client-only notices. |
| `Toast.Present` | None | Local-only. |
| `Console.Log` / `Console.AddLine` | None | Local-only. Use Multiplayer.Broadcast for replicated chat lines. |
| `ModalAlertController.Present*` | None | Local-only. No "show modal on every client" mechanism. |

---

## Init order pitfalls

1. **`WindowManager.Shared`, `Toast._instance`, `NoticeManager._shared`, `Console.shared`, `ModalAlertController.Shared`** all assigned in `Awake` of their respective MonoBehaviours. Pre-scene-load callers see `null`. `Multiplayer.Broadcast` checks `Host == null` but assumes `WindowManager.Shared != null` — calling `Broadcast` from a menu before scene load NREs at `WindowManager.Shared.Present`.
2. **Modal calls before `ModalAlertController.Shared` is set** throw `Exception("No ModalAlertController")` (`:30, 42, 122`).
3. **`NoticeManager.Shared` lazy-resolves via `FindObjectOfType`** — a scene without the component returns `null`. `NoticeExtensions.PostNotice` warns but doesn't throw; direct callers (PlayersManager) NRE.
4. **`Toast._instance` lazy-resolves via `FindObjectOfType`** — scene without component logs error and returns. Calls become no-ops.
5. **`Console.shared` lazy-resolves via the standard pattern.** `Console.Log` checks `shared != null` and silently drops if missing — useful for early init but means logs can vanish.
6. **`PostNoticeEphemeral` arriving before `NoticeManager.Shared` is initialized** would throw NRE on `NoticeManager.Shared.Handle`. Snapshot-restore order matters — see [Multiplayer Core § init order](multiplayer-core.md#init-order-pitfalls).
7. **`Multiplayer.Broadcast` from `OnPropertiesDidRestore`** is safe (host is up, WindowManager exists, Host != null in singleplayer because `PrepareHostIfNeeded` ran before connect). From `Awake` of an arbitrary MonoBehaviour, less safe.

---

## Surprising patches & non-obvious findings

1. **`Multiplayer.Broadcast` in singleplayer round-trips through `LocalGameClient`.** Even SP broadcasts have a 1-frame delay before they appear. Mods that broadcast then immediately read state will see stale state.
2. **`Multiplayer.SendError` defaults to `AlertLevel.Error`** which means `ToastPosition.Middle` (centered, intrusive). Pass `AlertLevel.Info` for `ToastPosition.Bottom` (less intrusive). Most mod errors should probably use `Info` unless they're truly disruptive.
3. **The Cancel-button platform reorder is substring-fragile.** Localized "Cancel" labels won't be reordered. Patch `UIPanelBuilder.AlertButtons` if you need locale-aware reordering.
4. **Console alerts ignore `AlertLevel`.** Both `Info` and `Error` render identically in the console buffer. To color-code, embed TMP markup in the message string.
5. **Notice cards are entirely independent of the Alert/Toast/Console pipeline** — they have their own MP message (`PostNoticeEphemeral`), their own UI surface (`NoticeRow`), their own audio cue. The only shared dependency is `Hyperlink.To` for the entity prefix.
6. **`PostEphemeralLocal` self-suppression check is correct** (despite the prior crib's flag) — `PlayerId.ToString()` is overridden to return the same value as `.String`. The check works as intended.
7. **`RequestRejected` IS invoked**, despite earlier docs marking it dead. `TrainController.HandleRequestSetIdent` emits it on validation failure. The pattern is: per-player `SendError` toast + global `SendFireEvent(default(RequestRejected))` for UI rebuild. Cross-update needed in [access-control.md › dead code](access-control.md#dead-codevestigial-inventory).
8. **`ModalAlertController.Present`'s `_inputString` is `private` instance state on the singleton** (`:18`) — not per-modal. **If two input modals are open simultaneously, both share the same `_inputString` field** and the second's typing overwrites the first's. (No vanilla code stacks input modals; modder beware.)
9. **`ModalAlert` modals can stack** as separate child instances of the singleton's canvas. There's no max, no z-order management beyond Unity child order.
10. **`Toast.Present` cancels in-flight tweens** — only one toast at a time. For multi-toast UX, you must build your own queue.
11. **No "save-format-migration" framework** exists. The single instance is `TutorialManager.Awake`'s `PresentOkay` for the legacy tutorial format. Mods that ship migrations should follow the same one-off `PresentOkay` pattern or build their own framework.
12. **`SaveManager.Save`/`Load` swallow exception details** — only the save name shows in the error modal. Exception stacks go to the log. Mods that need richer error UIs must patch.
13. **`Console.Log` is a `static class Console` in the global namespace** (`Console.cs:3`), not in a sub-namespace — easy to accidentally shadow `System.Console` when `using System;` and not `using System;` is rare. Most code paths wrap as `UI.Console.Console.shared.AddLine`.
14. **`AlertStyle` enum has only 2 values; adding a third (e.g., `Notification`) requires recompiling Assembly-CSharp.** Mods needing a third notification style must work within the existing two — typically by adding a substring sentinel to the message and patching `WindowManager.Present` to recognize it.
15. **No client-side rate-limit on Alerts.** Host can spam the network with broadcasts; clients will render every one. Console buffer is bounded by `_collapsed`/`expanded` view caps (in `ExpandedConsole` / `CollapsedConsole`, not surveyed here) but no producer-side throttle.
16. **`Multiplayer.SendError` to a remote player from a non-host machine NREs** — `Host` is null on clients. Always check `IsHost` or use `WindowManager.Shared.Present` directly for local toasts.

---

## Cross-references

- **Network transport, channel routing, INetworkMessage union, host-vs-client lifecycle:** [Multiplayer Core](multiplayer-core.md). Alert is union tag 12; PostNoticeEphemeral is an IGameMessage (different union).
- **Hyperlink emission, `EntityReference` URI codec, TextLinkReceiver, the dead `PassengerStop` handler, the Console-clickable vs Toast-non-clickable divergence:** [Hyperlink & EntityReference](hyperlink-entityref.md). This crib supersedes the suggestion that `PostEphemeralLocal` self-suppression is buggy.
- **Daily report broadcast emit pattern (`Multiplayer.Broadcast("A new daily report is available.")`):** [Daily Reports](daily-reports.md).
- **Console line buffer, hand-rolled vs registered console commands, `Console.Log` semantics:** [Console Commands](console-commands.md).
- **The legacy-tutorial `<saveformat-migration>` modal in `TutorialManager.Awake`:** [Tutorial](tutorial.md).
- **`HostOnlyAuthorizationRule` semantics, `[MinimumAccessLevel]`, `IPropertyAccessControlDelegate`:** [Access Control](access-control.md). **Update needed:** the "RequestRejected is dead code" claim should be revised to reflect `TrainController.HandleRequestSetIdent` as an active emitter and `CarCustomizeWindow.RebuildUponRequestReject` as the consumer.
- **`StateManager.ApplyLocal`, `SendFireEvent`, message dispatch:** see related State Manager / Request Messages cribs (referenced from multiplayer-core).
- **`PlayerId.ToString` override resolves the suspected hyperlink-entityref bug.** PlayerId.cs:23 returns `_playerId`; same as `.String` getter. The self-suppression check `entity.Id == PlayersManager.PlayerId.ToString()` is correct.
