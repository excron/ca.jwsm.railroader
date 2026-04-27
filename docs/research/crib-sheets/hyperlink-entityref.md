# Hyperlink & EntityReference — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Console Commands](console-commands.md), [Players & TrainCrews](players-traincrew.md), [Economy](economy.md), [Passengers & Timetable](passengers-timetable.md), [Events Catalog](events-catalog.md)

Railroader's hyperlink system is **eight types deep, two files wide, and entirely unextensible without Harmony**. `Hyperlink` is a `readonly struct` whose `ToString()` emits a TextMeshPro `<link="…"><style=ConsoleLink><noparse>label</noparse></style></link>` triple — that's it. `EntityReference` is the URI form (`type:id`); `SerializableEntityReference` is the `MessagePack` wire format used by ledger entries and notice posts. The link **address scheme is a hard-coded switch** in `EntityReference.URI()`/`TryParseURI()` (8 cases) and `LinkDispatcher.Open(EntityReference)` (8 cases plus an `http(s):` short-circuit). `TextLinkReceiver` is the per-`TMP_Text` MonoBehaviour that hit-tests clicks via `TMP_TextUtilities.FindIntersectingLink`; in `UIPanelBuilder` it's added on demand by `AddTextLinkReceiverIfNeeded` only when the rendered text contains the literal substring `"<link"`. The console line prefab has the receiver built into its asset; chat broadcasts come over the network as `Network.Messages.Alert(AlertStyle.Console, …)` which feeds `Console.AddLine` and gets rendered by the same prefab. **Entity ids are content keys, not network handles** — `car.id` and `industry.identifier` are stable strings that resolve to the same object on every client (host-authoritative), so links survive serialization and are MP-stable in the trivial sense. There is no opaque "link handle" or generational id — links are just typed string tuples.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Hyperlink` (readonly struct) | `Hyperlink.cs:8` | `(Address, Text)` pair; `ToString()` emits TMP rich text; implicit cast to string |
| `Hyperlink.To(...)` (8 overloads) | `Hyperlink.cs:24-72` | The constructor surface — one per common entity type plus `Transform` for ad-hoc Position links |
| `EntityReference` (struct) | `EntityReference.cs:8` | `(EntityType Type, string Id)`; codecs `URI()`, `TryParseURI()`, `Text()` |
| `EntityType` (enum) | `EntityType.cs:1` | 8 values: `Industry=1, PassengerStop, Car, Player, Position, Timetable, Help, Crew` |
| `SerializableEntityReference` (struct) | `SerializableEntityReference.cs:4` | `[MessagePackObject(false)]` wire form for `Ledger`/`Notice` |
| `Helpers.LinkDispatcher.Open(string)` | `Helpers/LinkDispatcher.cs:20` | Resolves URI → window/action; the click-routing chokepoint |
| `UI.TextLinkReceiver` | `UI/TextLinkReceiver.cs:11` | Per-TMP_Text click hit-test → `LinkDispatcher.Open` (or override) |
| `UIPanelBuilder.AddTextLinkReceiverIfNeeded(label, text)` | `UI.Builder/UIPanelBuilder.cs:234` | Adds receiver iff text contains `"<link"` substring |
| `Game.Notices.NoticeManager` | `Game.Notices/NoticeManager.cs:11` | Notice card surface; ephemeral `EntityReference`-keyed banners |
| `Network.Messages.Alert` (`AlertStyle.Console`) | `Network.Messages/Alert.cs:6` | Wire format for `Multiplayer.Broadcast` chat lines |
| `Console.ConsoleEscape(this string)` | `Console.cs:14` | Wraps user-supplied strings in `<noparse>…</noparse>` to neutralise stray markup |

---

## Spine: how a link is born, transmitted, rendered, and clicked

```
HOST                                                CLIENT
─────                                                ──────
Foo.Bar()
  Hyperlink h = Hyperlink.To(car);                  ← Hyperlink.cs:42
    new Hyperlink("car:" + car.id, car.DisplayName)
    h.ToString() → "<link=\"car:LV-1234\">
                   <style=ConsoleLink>
                   <noparse>LV 1234 box</noparse>
                   </style></link>"

Multiplayer.Broadcast($"{h} received cargo")        ← Network/Multiplayer.cs:181
  new Alert(AlertStyle.Console, Info, msg, ts)
  Host.SendToAll(alert)  ───────────────────────►   ApplyHostMessage(alert)
                                                     WindowManager.Present(alert)
                                                       case AlertStyle.Console:
                                                         Console.shared.AddLine(msg, ts)
                                                           expanded.Add(entry)
                                                             → ConsoleLinePool.CreateLine
                                                               (TMP prefab w/ TextLinkReceiver
                                                                already attached as asset)
                                                                 tmp.text = msg

User clicks line:
  TextLinkReceiver.OnPointerClick                   ← UI/TextLinkReceiver.cs:22
    TMP_TextUtilities.FindIntersectingLink(_text, position)
    string link = textInfo.linkInfo[idx].GetLink()  → "car:LV-1234"
    OnLinkClicked != null
      ? OnLinkClicked(link)                          ← AddTextArea() override path
      : LinkDispatcher.Open(link)                    ← default path

LinkDispatcher.Open(string link)                    ← Helpers/LinkDispatcher.cs:20
  if (StartsWith http(s):)  Application.OpenURL
  else if (TryParseURI → r) Open(r)
       Open(EntityReference)                        ← LinkDispatcher.cs:43
         switch (r.Type)
           Help          → GuideWindow.Show(id)
           Industry      → CompanyWindow.Shared.ShowIndustry(id)
           PassengerStop → Log.Error("not supported")  ← DEAD HANDLER
           Car           → if (Shift) TC.SelectedCar = car
                           else CarInspector.Show(car)
           Crew          → CompanyWindow.Shared.ShowCrew(id)
           Player        → CompanyWindow.Shared.ShowPlayer(id)
           Position      → CameraSelector.JumpToPoint(v4 → pos+rotY)
           Timetable     → TimetableWindow.Shared.Show()
```

**Three things to internalise:**

1. **Vanilla never round-trips an `EntityReference` over the network.** What goes over the wire is the *rendered string* — the entire `<link=…>…</link>` block sits inside `Alert.Message`, `Say.text` (chat fallback), `PostNoticeEphemeral.Content` does **not** contain a hyperlink (the entity is sent as a separate `SerializableEntityReference` field), and `Ledger.Entry.Payee` is a `SerializableEntityReference?`. So replication is "the host emitted text containing literal `<link=…>` already pointing at content keys; clients render and dispatch locally." Stable across clients because content keys (`car.id`, `industry.identifier`, `PlayerId.String`) are stable.

2. **The only way to add a new EntityType is Harmony.** `EntityType` is a closed enum; both `URI()`/`TryParseURI` and `LinkDispatcher.Open` use exhaustive `switch` statements that throw `ArgumentOutOfRangeException` on unknown values. There is no registry, no `IEntityResolver`, no late-bound attribute. Any added type requires patching at minimum: `EntityReference.URI` (default-throw branch), `EntityReference.TryParseURI` (default-return-false branch), `EntityReference.Text` (default `"Unknown"`), `LinkDispatcher.Open(EntityReference)` (default-throw), and ideally `Hyperlink.To` overload. See [Patch points for custom types](#patch-points-for-custom-entity-types).

3. **Custom link handlers ARE supported per-receiver.** `TextLinkReceiver.OnLinkClicked` is a public `Action<string>`. If non-null it fires *instead of* `LinkDispatcher.Open`, so a panel can intercept its own link clicks (`UIPanelBuilder.AddTextArea(text, onLinkClicked)` is the vanilla example). This works for **arbitrary** address schemes — your text can contain `<link="mod:foo">…</link>` and your handler will get `"mod:foo"`. No patching required as long as you control the receiver.

---

## `Hyperlink` — the rich-text producer

```csharp
public readonly struct Hyperlink(string address, string text) {        // Hyperlink.cs:8
    public readonly string Address = address;
    public readonly string Text    = text;
    public override string ToString()
        => "<link=\"" + Address + "\"><style=ConsoleLink><noparse>" + Text + "</noparse></style></link>";
    public static implicit operator string(Hyperlink v) => v.ToString();
}
```

### `Hyperlink.To` overloads — the entire factory surface

| Overload | Address scheme | Label source | File:line |
|---|---|---|---|
| `To(IPlayer player)` | `player:{player.PlayerId}` | `player.Name` | `Hyperlink.cs:24` |
| `To(PlayerId playerId)` | `player:{playerId}` | `PlayersManager.NameForPlayerId(id)` | `Hyperlink.cs:29` |
| `To(Industry industry, string name = null)` | `industry:{identifier}` | `name ?? industry.name` | `Hyperlink.cs:35` |
| `To(Car car)` | `car:{car.id}` | `car.DisplayName` | `Hyperlink.cs:40` |
| `To(Car car, string name)` | `car:{car.id}` | `name ?? car.DisplayName` | `Hyperlink.cs:45` |
| `To(IOpsCar car)` | `car:{car.Id}` | `car.DisplayName` | `Hyperlink.cs:50` |
| `To(Transform tx, string text)` | `pos:{x},{y},{z},{rotY}` (rounded ints) | caller-supplied | `Hyperlink.cs:55` |
| `To(EntityReference r)` | `r.URI()` | `r.Text()` | `Hyperlink.cs:62` |
| `To(PassengerStop ps)` | **forwards to `To(GetComponentInParent<Industry>())`** | parent industry name | `Hyperlink.cs:69` |

**Conspicuously missing overloads** (these are not `Hyperlink.To` candidates — callers must build the `Hyperlink` or `EntityReference` directly):

- `To(TrainCrew)` — Crew links exist (`EntityType.Crew`, `LinkDispatcher` opens the crews tab), but there's no factory. Vanilla doesn't construct one anywhere — the *only* place a `crew:` URI is generated in vanilla is via `EntityReference(EntityType.Crew, id)` build-by-hand (and even that has zero call sites; `Crew` is technically dead-on-emit in vanilla, only the receiver works). See [Dead handlers and never-emitted types](#dead-handlers-and-never-emitted-types).
- `To(Help id)` — same story: no factory; `EntityType.Help` is wired in `LinkDispatcher` but the only emitter is `LinkDispatcher.Open(EntityType.Help, "timetables")` called directly from `VisualTimetableEditor.cs:118` — that path constructs an `EntityReference`, not a Hyperlink.
- `To(Timetable)` — built ad-hoc as `Hyperlink.To(new EntityReference(EntityType.Timetable, null))` (`TimetableController.cs:319`). The id is **null**; `Text()` returns the literal string `"Timetable"`.

### TMP markup contract

```
<link="ADDRESS"><style=ConsoleLink><noparse>LABEL</noparse></style></link>
```

- `ConsoleLink` is a TMP **style** defined in the project's TMP style asset (not in code). Color/underline/hover come from the style sheet.
- `<noparse>` is critical — it neutralises any `<` or `>` in the label that would otherwise be parsed as further markup. **`Hyperlink` is unconditionally wrapping the label** in `<noparse>`, so labels like `"BCR <Owner>"` render correctly.
- The address is **double-quoted** (`<link="addr">`). This means the address itself must not contain a literal `"`. None of the eight address schemes can produce a `"` (PlayerId is digits, identifier strings are configurable but conventionally `[a-z0-9_-]+`, position is a comma-separated int tuple). If you ever build a custom address that might contain `"`, you'll silently break the markup.
- `Hyperlink.ToString()` does **not** escape the address. A maliciously chosen `Industry.identifier` containing `"><script…` would emit it verbatim into the rich text — but `<noparse>` only protects the label, not the address. TMP doesn't execute scripts; the consequence is broken markup, not RCE.

### Patch candidates (Hyperlink)

| Method | Why patch |
|---|---|
| `Hyperlink.ToString()` | Replace the markup template (e.g., add tooltips via `<voffset>` data, change `ConsoleLink` style name, drop `<noparse>` for advanced styling). Pure formatting; no callers depend on substring layout. |
| `Hyperlink.To(IPlayer)` / `To(PlayerId)` | Inject access-level badges in the label, prepend Steam avatar TMP sprites. |
| `Hyperlink.To(Car)` | Add reporting-mark prefix, condition glyph, etc. |
| Add new `Hyperlink.To(YourType)` | Either via Harmony (overload-add isn't really doable; emit your own `Hyperlink` ctor) **or** simpler: ship a static helper `MyHyperlinks.To(...)`. The struct ctor is public. |

### MP authority

- None. `Hyperlink` is a pure-string constructor; no replication or auth surface. Its outputs travel inside `Alert.Message`, `Say.text`, etc., which have their own auth.

### Gotchas

- **`Hyperlink` is a `readonly struct`.** Implicit cast to `string` exists, so `$"... {h} ..."` works in interpolation, but `(string)h` and direct `.ToString()` are also valid. Don't `.Equals(other)` two Hyperlinks expecting URI-comparison — it's structural over `(Address, Text)`.
- **`Hyperlink.To(PassengerStop ps)` deliberately upgrades to the parent industry.** There is no `passstop:` link emitter in vanilla code — `PassengerStop`-typed `EntityReference`s do exist (used as ledger payees in `PassengerStop.PayPassengerFare`), but they only travel through `Ledger.Entry.Payee`, not as clickable links. Even if you produced one, `LinkDispatcher.Open` logs `"Open passenger stop link not supported"` and bails. See [Dead handlers](#dead-handlers-and-never-emitted-types).
- **`To(Transform, text)` rounds coordinates to ints** (`Mathf.RoundToInt(x/z)`, `CeilToInt(y)`, `RoundToInt(rotY)`). For sub-meter precision you'd build `EntityReference(EntityType.Position, "...")` with a custom `Id` string and round-trip through `TryParseVector4` — but **`TryParseVector4` only accepts ints** (`int.TryParse`, not `float.TryParse`), so half-meter precision is unreachable via this path.
- **`To(Car, string)` does *not* let you override the URI.** Only the label changes; the link still resolves to the car. To produce a "car-flavoured but pointing elsewhere" link you must construct `new Hyperlink(addr, label)` directly.
- **Implicit string conversion is one-way.** `string s = hyperlink;` works; `Hyperlink h = s;` does not.

---

## `EntityReference` — the URI codec

```csharp
public struct EntityReference {                                        // EntityReference.cs:8
    public EntityType Type;
    public string     Id;
    public EntityReference(EntityType, string)
    public EntityReference(SerializableEntityReference)
    public EntityReference(PlayerId)                                   // → (Player, playerId.String)
    public EntityReference(EntityType, Vector4)                        // for Position; ints stringified
    public bool TryParseVector4(out Vector4)
    public string URI()                                                // throws on unknown Type
    public static string URI(EntityType, string)                       // = new(t,i).URI()
    public static bool TryParseURI(string link, out EntityReference)   // returns false on bad scheme
    public string Text()                                               // resolves displayable name
}
```

### URI scheme

| `EntityType` | Numeric | URI prefix | `Id` format | Resolved text |
|---|---|---|---|---|
| `Industry` | 1 | `industry:` | `Industry.identifier` (string) | `OpsController.Shared.AllIndustries.First(i.identifier == id)?.name` |
| `PassengerStop` | 2 | `passstop:` | `PassengerStop.identifier` | `PassengerStop.FindAll().First(...)?.DisplayName` |
| `Car` | 3 | `car:` | `Car.id` (e.g. `"LV-1234"`) | `TrainController.Shared.CarForId(id)?.DisplayName` |
| `Player` | 4 | `player:` | `PlayerId.String` (Steam id "D" format) | `PlayersManager.PlayerForId(new PlayerId(id))?.Name` |
| `Position` | 5 | `pos:` | `"x,y,z,rotY"` 4 ints | `"Unknown"` (no resolver — `Text()` returns "Unknown" for Position) |
| `Timetable` | 6 | `tt:` | `null` (vanilla emits null) | `"Timetable"` (literal) |
| `Help` | 7 | (URI emit not supported) | `GuideWindow` link id | `"Unknown"` (no `Text()` case) |
| `Crew` | 8 | `crew:` | `TrainCrew.Id` (Guid string) | `"Unknown"` (no `Text()` case) |

Three asymmetries jump out:

- **`EntityType.Help` has *no* `URI()` case** — `EntityReference.URI()` throws `ArgumentOutOfRangeException` for `Help`. But `TryParseURI` accepts the literal prefix `"help:"`. This is a **one-way handler**: links can be authored manually (the only vanilla emitter is `VisualTimetableEditor.cs:118` calling `LinkDispatcher.Open(EntityType.Help, "timetables")`, which never goes through `URI()`), but never round-tripped through the codec. **Do not call `EntityReference.URI()` on a Help reference — it will throw.**
- **`Text()` only has cases for Industry, Car, PassengerStop, Player, Timetable.** Position, Help, Crew all fall to `_ => "Unknown"`. So `Hyperlink.To(new EntityReference(EntityType.Crew, "abc")).Text` is `"Unknown"`, not the crew name.
- **`EntityType` enum is `1`-based**, not the C# default `0`. `default(EntityType)` is `0` which is *not* a valid value — all `URI()`/`Text()` switches will hit their default arm. `EntityReference()` (parameterless) yields `Type=0, Id=null`, which throws on `URI()` and resolves to `"Unknown"` on `Text()`.

### URI parsing

```csharp
public static bool TryParseURI(string link, out EntityReference r) {
    int colon = link.IndexOf(':');
    if (colon == -1) { r = default; return false; }
    string prefix = link.Substring(0, colon);
    string id = link.Substring(colon + 1);
    EntityType type;
    switch (prefix) { /* the 8-prefix table; default → r=default, return false */ }
    r = new EntityReference(type, id);
    return true;
}
```

- **First `:` wins.** A position id like `"1,2,3,4"` doesn't contain `:`, but if a future prefix were ever to use a colon-bearing id, it would be silently truncated.
- The `id` substring is whatever follows the *first* colon, even if empty. `"car:"` parses to `(Car, "")` and successfully returns `true`.
- `http://` and `https://` would parse as `(prefix="http", id="//…")` and *fail* the prefix-switch (returning `false`), so `LinkDispatcher`'s explicit `StartsWith("http:")` check ahead of `TryParseURI` is necessary, not redundant.

### Patch candidates (EntityReference)

| Method | Why patch |
|---|---|
| `EntityReference.URI()` | Add a case for your custom `EntityType`; **add a `Help` case** to fix the missing-URI bug. |
| `EntityReference.TryParseURI(string, out EntityReference)` | Add inbound prefix mapping for your custom types. Without this, `LinkDispatcher.Open(yourLink)` logs `"Failed to parse link"` and bails. |
| `EntityReference.Text()` | Resolve display strings for custom types and for the four currently-`"Unknown"` cases (Position, Help, Crew, plus your additions). |

### MP authority

- Pure data + pure functions. No replication. Travels inside `SerializableEntityReference` for ledger/notice; otherwise lives in stringified-link form.

### Gotchas

- **`URI()` throws on unknown type.** `try/catch` if you're calling it on user-provided data.
- **`TryParseURI` does not validate the id**, only the prefix. `(Car, "garbage-id")` parses successfully; the bad id only surfaces when `LinkDispatcher.Open` runs `TrainController.TryGetCarForId` and toasts "Car not found."
- **`Text()` is `OpsController.Shared?.AllIndustries`-aware** — if called before world load (no `OpsController`), Industry returns `"Unknown Industry"`. PassengerStop's `PassengerStop.FindAll()` is a global FindObjectsOfType — expensive in tight loops; cache.
- **The PlayerId ctor `EntityReference(PlayerId)` always sets `EntityType.Player`.** It's a convenience. Equivalent to `new EntityReference(EntityType.Player, playerId.String)`.

---

## `SerializableEntityReference` — the wire format

```csharp
[MessagePackObject(false)]                                             // SerializableEntityReference.cs:4
public struct SerializableEntityReference {
    [Key("type")] public EntityType Type;
    [Key("id")]   public string Id;
    public SerializableEntityReference(EntityReference)
    public SerializableEntityReference(EntityType, string)
}
```

Used by:

- `Game.Messages.PostNoticeEphemeral.Entity` (HostOnly auth) — host posts notice cards keyed to entities.
- `Game.State.SerializableLedgerEntry.Payee` (`SerializableEntityReference?`) — every ledger entry's payee field.
- Snapshot `LedgerEntry` array (transitively through `SerializableLedgerEntry`).

**Note the keys are *string*-keyed (`"type"`, `"id"`) rather than int-keyed.** Most other Railroader MessagePack structs use `[Key(0)]` int keys; SerializableEntityReference uses string keys, making the wire payload slightly more verbose but more debuggable. Don't "optimise" this — it's persisted in save files via the ledger.

### Gotchas

- **Round-trip is value-only**, no validation. A save file or remote message can contain `Type = (EntityType)999, Id = null`. Resolving via `new EntityReference(serialized)` succeeds; `URI()` throws and `Text()` returns `"Unknown"`. If you mod-add an `EntityType` value, **older clients deserializing newer saves will see `(EntityType)yourValue, your_id` and treat it as garbage** — there is no migration story.
- **MessagePack default for `EntityType` is `(EntityType)0`** — same gotcha as in-memory `EntityReference`. A field omitted in the wire will deserialize to `Industry`-minus-one (i.e., not Industry — `0` is not a valid case).

---

## `Helpers.LinkDispatcher` — the click router

```csharp
public static class LinkDispatcher {                                   // Helpers/LinkDispatcher.cs
    public static void Open(Hyperlink link)                            // delegates to Open(string)
    public static void Open(string link)                               // http(s) → OpenURL; else TryParseURI → Open(r)
    public static void Open(EntityType, string)                        // = Open(new EntityReference(t,i))
    private static void Open(EntityReference r)                        // the big switch
}
```

### Per-type behaviours

| `EntityType` | Action | File:line |
|---|---|---|
| `Help` | `GuideWindow.Show(id)` (jumps to anchor) | `LinkDispatcher.cs:48` |
| `Industry` | `CompanyWindow.Shared.ShowIndustry(id)` (sets `_selectedTabState="locations"` + `_selectedLocationsItem=id`) | `LinkDispatcher.cs:51` |
| `PassengerStop` | `Log.Error("Open passenger stop link not supported")` — **dead handler** | `LinkDispatcher.cs:53` |
| `Car` | `Shift` held → `TrainController.SelectedCar = car`; else `CarInspector.Show(car)`; missing → `Toast.Present("Car not found.")` | `LinkDispatcher.cs:58` |
| `Crew` | `CompanyWindow.Shared.ShowCrew(id)` | `LinkDispatcher.cs:76` |
| `Player` | `CompanyWindow.Shared.ShowPlayer(id)` | `LinkDispatcher.cs:79` |
| `Position` | `TryParseVector4` → `CameraSelector.shared.JumpToPoint(v, Quaternion.Euler(0, w, 0), Strategy)`; throws `ArgumentException` on parse failure | `LinkDispatcher.cs:83` |
| `Timetable` | `TimetableWindow.Shared.Show()` (id ignored) | `LinkDispatcher.cs:91` |

### Patch candidates (LinkDispatcher)

| Method | Why patch |
|---|---|
| `LinkDispatcher.Open(EntityReference)` (private) | Add cases for new `EntityType` values, replace existing window dispatches (e.g., open your own car inspector instead of `CarInspector.Show`), revive the dead `PassengerStop` handler. |
| `LinkDispatcher.Open(string)` (public) | Intercept *any* link click globally — useful for URL schemes (e.g., `mod:discord` → open Discord). The `http(s):` branch fires before `TryParseURI`; you can prefix-check your own scheme(s) ahead of the fallthrough. |
| `LinkDispatcher.Open(Hyperlink)` | Cosmetic; just delegates to `Open(string)`. Patch `Open(string)` instead. |

### MP authority

- All operations are local-only. `LinkDispatcher` is a UI router; nothing it does requires host authority. Opening a Player profile or jumping the camera doesn't replicate.

### Gotchas

- **`Open(EntityReference)` is `private`.** Use `Open(EntityType, string)` (public, equivalent) or `Open(string)` (public, parses the URI). Reflection works if you really need the struct overload.
- **Shift-modifier on Car** is a quirk: shift-click selects in the world (`TrainController.SelectedCar`), normal click opens inspector. Don't assume a click *always* opens a window.
- **PassengerStop is mute.** A link generated as `passstop:foo` produces a console error, no toast, no window. The vanilla `Hyperlink.To(PassengerStop)` factory deliberately upgrades to the parent industry to dodge this.
- **`CompanyWindow.Shared`, `GuideWindow.Instance`, `TimetableWindow.Shared`** all rely on `WindowManager.Shared.GetWindow<T>()` — fine post-scene-load, NRE pre-load. Don't trigger LinkDispatcher from `Awake` or pre-`StateRequiredOnLoad` hooks.
- **`http://` and `https://` are the only non-entity schemes.** `mailto:`, `steam://`, etc. all fail through to `TryParseURI` → "Failed to parse link". Patch `Open(string)` if you need more.

---

## `UI.TextLinkReceiver` — the TMP click hit-tester

```csharp
[DisallowMultipleComponent]
[RequireComponent(typeof(TMP_Text))]
public class TextLinkReceiver : MonoBehaviour, IPointerClickHandler { // UI/TextLinkReceiver.cs:11
    private TMP_Text _text;
    public Action<string> OnLinkClicked;                              // override hook

    public void OnPointerClick(PointerEventData ev) {
        int idx = TMP_TextUtilities.FindIntersectingLink(_text, ev.position, camera: null);
        if (idx == -1) return;
        string link = _text.textInfo.linkInfo[idx].GetLink();
        if (OnLinkClicked != null) OnLinkClicked(link);
        else                       LinkDispatcher.Open(link);
    }
}
```

### Where receivers come from

1. **Console line prefab** — the TMP prefab fed to `ConsoleLinePool.CreateLine` has `TextLinkReceiver` baked in as an asset component. `Console.AddLine` strings flow straight into a TMP that already hit-tests.
2. **Notice card label** — `NoticeRow.label` (TMP_Text). The link in `LabelTextForNotice` is the *entity hyperlink* prepended to user content (`<style=b>{Hyperlink.To(entity)}</style>  <style=p>{content}</style>`). Whether this label has the receiver depends on the **NoticeRow prefab**, which the decompile shows but the asset binding lives in Unity. Empirically rows are clickable.
3. **`UIPanelBuilder.AddTextLinkReceiverIfNeeded(label, text)`** (`UI.Builder/UIPanelBuilder.cs:234`):
   ```csharp
   if (!string.IsNullOrEmpty(text) && text.Contains("<link"))
       label.gameObject.AddComponent<TextLinkReceiver>();
   ```
   Called from every `AddLabel(string)` / `AddTextLabel(markup)` overload (`UIPanelBuilder.cs:205, 213, 221, 229`). **The substring check is the literal `"<link"` — case-sensitive, no whitespace tolerance.** Hyperlink emits `<link="…"` so it always matches. Custom code that emits e.g. `<LINK="…">` would slip past and not get a receiver.
4. **`UIPanelBuilder.AddTextArea(text, onLinkClicked)`** (`UIPanelBuilder.cs:255`) — the prefab `_assets.textArea` always has the receiver; the `onLinkClicked` callback overrides `LinkDispatcher`. This is the one path that **swaps** dispatch.

### Patch candidates (TextLinkReceiver)

| Method | Why patch |
|---|---|
| `TextLinkReceiver.OnPointerClick` | Intercept all link clicks at the UI layer. Wrap `link` to add modifier-key behaviours (e.g., right-click context menu, alt+click copy address). |
| `UIPanelBuilder.AddTextLinkReceiverIfNeeded` | Broaden the substring check, attach receiver always, add a default-handler. |

### Gotchas

- **`OnLinkClicked` short-circuits `LinkDispatcher`.** If you set the field, you take *full* responsibility for handling — no fallthrough. To "augment" without losing default behaviour: `receiver.OnLinkClicked = link => { MyHandler(link); LinkDispatcher.Open(link); };`.
- **Click hit-testing uses the screen-space TMP utility with `camera: null`** — assumes overlay canvas. Worldspace canvases will silently miss-click. This is fine for vanilla (all dispatch is overlay UI); mod worldspace UIs need a custom receiver.
- **No drag/hover events.** Link rollover styling would have to be implemented separately via `TMP_TextEventHandler` or similar.
- **`AddTextLinkReceiverIfNeeded` does not deduplicate.** It calls `AddComponent<TextLinkReceiver>` unconditionally when `<link` is present, but the receiver has `[DisallowMultipleComponent]`, so a second add silently no-ops at runtime (Unity warning emitted). If you re-`SetTextMarkup` a label whose original text had no `<link` and the new one does, `AddTextLinkReceiverIfNeeded` won't be re-invoked — the label stays inert. The vanilla pattern is "build label once; rebuild whole panel via `RebuildOnEvent` if content changes."

---

## `Game.Notices.NoticeManager` — the notice card surface

```csharp
public class NoticeManager : MonoBehaviour {                          // Game.Notices/NoticeManager.cs:11
    public static NoticeManager Shared { get; }                       // FindObjectOfType, cached
    public void PostEphemeral(EntityReference entity, string key, string content)   // host-only
    public void PostEphemeralLocal(EntityReference entity, string contextualKey, string content)
    public void Handle(PostNoticeEphemeral post)                       // dispatch from network
    public void Clear()                                                // wipe all rows
    private string LabelTextForNotice(EntityReference, string content)
}
```

### Wire format

`Game.Messages.PostNoticeEphemeral` (`HostOnlyAuthorizationRule`):
```csharp
struct PostNoticeEphemeral { SerializableEntityReference Entity; string Key; string Content; }
```

### Behaviour

- **Host-only post.** `PostEphemeral` calls `StateManager.AssertIsHost()`. Clients call `Handle(…)` when the message arrives, which flows into `PostEphemeralLocal`.
- **Self-suppression**: if `entity.Type == Player && entity.Id == myPlayerId`, the notice is silently dropped on the local machine (`NoticeManager.cs:68`). Host-broadcast "Player X connected" notices don't echo to X themselves.
- **Coalescing key**: `$"{(int)entity.Type}//{entity.Id}//{contextualKey}"`. Posting the same key with the same content is a no-op; with different content, the existing row is dismissed and a new one shown. Empty content dismisses.
- **Audio**: every fresh post plays the local one-shot `"telegraph-ditdit"` via `ScheduledAudioPlayer.PlaySoundLocal`.
- **Label format**: `<style=b>{Hyperlink.To(entity)}</style>  <style=p>{content}</style>` — entity is the bold link prefix, content is the body text. The body is **not** wrapped in `<noparse>`; it's free TMP markup. **If your content needs to embed a player name or user-supplied string, wrap with `.ConsoleEscape()` (`<noparse>...</noparse>`) yourself.**
- **Convenience**: `NoticeExtensions.PostNotice(this Car car, key, content)` (`NoticeExtensions.cs:8`) — only an extension on `Car` exists in vanilla. Other entity types must call `NoticeManager.Shared.PostEphemeral(...)` directly.

### Patch candidates (NoticeManager)

| Method | Why patch |
|---|---|
| `NoticeManager.PostEphemeralLocal` | Mod-side notice routing — silence specific keys, batch, route to your own UI. |
| `NoticeManager.LabelTextForNotice` | Customize the entity-prefix style. |
| `NoticeManager.Handle` | Intercept network notices; selectively suppress. |

### Gotchas

- **Host-only emission.** Clients cannot post their own notices via this system. There is no `RequestPostNotice`. If a client mod wants to surface a notice to itself only, call `NoticeManager.Shared.PostEphemeralLocal(…)` directly (no auth check).
- **Empty content = dismiss.** `PostNotice("foo", null)` or `""` removes the row. Use this to clear; do not call a separate `Dismiss` method (none exposed except internal `DismissRow`).
- **Audio is local-only.** `ScheduledAudioPlayer.PlaySoundLocal` plays on the receiving client. Multiple notices in quick succession overlap.
- **Coalescing is per (Type, Id, contextualKey).** Two different cars with the same `key` are independent rows; the same car with two `key`s is two rows.
- **Self-suppression is by `entity.Id == PlayersManager.PlayerId.ToString()`** which calls `PlayerId.ToString()` (the struct's default `ToString`). `PlayerId.ToString` is not overridden in the visible decompile — it returns the system default (`"PlayerId"` or autogen). **This may be a bug**: the comparison probably should be against `PlayersManager.PlayerId.String`. Test before relying on self-suppression.

---

## Chat & broadcast pipeline (where Hyperlinks live in MP messages)

| Context | Wire message | Renders via | Notes |
|---|---|---|---|
| User-typed chat | `Game.Messages.Say` (`Passenger`-min, `ICharacterMessage`) | Host wraps as `Console.Log($"{Hyperlink.To(sender)}: {text.ConsoleEscape()}")` (`StateManager.cs:1036`). The sender hyperlink is built host-side; the message text is `<noparse>`-wrapped. | The wrapped string is then `Console.Log`'d locally on the host. **Other clients only see chat when the host re-broadcasts via `Multiplayer.Broadcast`?** Actually no — `Console.Log` is local only. Chat replication path is via `_playersManager` echoing back through normal char-message dispatch — see [Players & TrainCrew](players-traincrew.md). The hyperlink generation here is host-side. |
| System broadcast | `Network.Messages.Alert(AlertStyle.Console, AlertLevel.Info, msg, ts)` | `Multiplayer.Broadcast(msg)` (`Network/Multiplayer.cs:181`); receiver: `WindowManager.Present(alert)` → `Console.shared.AddLine(alert.Message, ts)` | This is the workhorse. Producers: ops payments, repairs, train crew changes, AI Say lines, equipment purchase, timetable updates, host promote/demote/ban, etc. The string travels with full TMP markup; clients render directly. |
| AI "Say" lines | `Multiplayer.Broadcast` from `AutoEngineerPlanner.Say` (`AutoEngineerPlanner.cs:973`): `$"Auto Engineer {Hyperlink.To(_locomotive, overrideName)}: \"{message}\""` | Same as system broadcast | The `_locomotive` hyperlink is built host-side (the AI runs on host). Signal/passenger-stop hyperlinks are baked into `message` by the caller (e.g., `AutoEngineerPlanner.cs:766` for signals as Position links via `To(signal.transform, text)`; `AutoEngineerPassengerStopper.cs:313, 335, 351, 367, 371` for `To(_nextStop)`). |
| Single-target alert | `Network.Messages.Alert(AlertStyle.Toast, level, msg, ts)` | `Multiplayer.SendError(player, msg)` etc. → `WindowManager.Present` → `Toast.Present` | **Toasts use TMP_Text too**, but the `Toast` MonoBehaviour does NOT add a `TextLinkReceiver` (`Toast.cs:13` — just a `TextMeshProUGUI` field). Hyperlinks in toast text **render visually but are non-clickable**. |
| Notice card | `PostNoticeEphemeral(SerializableEntityReference, key, content)` | `NoticeManager.Handle` | The entity ref is a structured field, not embedded in content. Content is TMP markup that renders with the `Hyperlink.To(entity)` bold prefix. |
| Console output (`Console.Log`) | local only (no wire) | Console line prefab (clickable) | Used inside command handlers; output stays on the runner's screen. |

### Patch candidates (chat layer)

| Method | Why patch |
|---|---|
| `Network.Multiplayer.Broadcast(string)` | The single chokepoint for system broadcasts. Postfix to log-tap, prefix to filter/rewrite (e.g., strip hyperlinks for accessibility, rewrite player names, route to Discord webhook). |
| `Multiplayer.SendError(player, msg)` | Toast-level errors. Same idea, single-target. |
| `WindowManager.Present(Alert)` | Receiver-side intercept; lets you choose to render Console-style messages as toasts or vice versa. |
| `Toast` (asset) | If you want toast hyperlinks clickable, attach a `TextLinkReceiver` to the toast prefab (or postfix `Toast.Present` with `gameObject.AddComponent`). |

---

## Dead handlers and never-emitted types

| Type | Status | Notes |
|---|---|---|
| `EntityType.Help` | **One-way**: `LinkDispatcher` opens `GuideWindow.Show(id)`, but `EntityReference.URI()` throws on `Help`. The only emitter (`VisualTimetableEditor.cs:118`) bypasses `URI()` entirely. **Patch `URI()` to add a `Help => "help"` case** if you want to construct Help links via the codec. |
| `EntityType.PassengerStop` | **Dead receiver**: the `LinkDispatcher.Open` case logs an error and does nothing. The only PassengerStop `EntityReference` constructed in vanilla is for ledger-payee bookkeeping (`PassengerStop.cs:1021`), which never round-trips to a clickable link. The `Hyperlink.To(PassengerStop)` factory deliberately *avoids* emitting one. **Patch the dispatcher case** (e.g., to call `CompanyWindow.Shared.ShowIndustry(ps.GetComponentInParent<Industry>().identifier)` plus a scroll/highlight). |
| `EntityType.Crew` | **No emitter**: `LinkDispatcher.Open` works; `EntityReference.URI()`/`TryParseURI` work; **no vanilla code calls `Hyperlink.To` or `new EntityReference` for `Crew`.** Only `LinkDispatcher.Open(EntityType.Crew, trainCrewId)` is called directly from `BuilderExtensions.cs:91` (the "Show" button next to the Train Crew dropdown), bypassing both `Hyperlink` and `EntityReference`. So `Crew` has no clickable links anywhere in vanilla output — you'd need to mod-add a `Hyperlink.To(TrainCrew)`. |
| `EntityType.Position` | **Live, mostly used by AI signals**: `Hyperlink.To(Transform, text)` is the only ergonomic factory. **`Text()` returns `"Unknown"` for Position** — there's no resolver. A position link's hover text shows whatever the emitter wrote. |
| `EntityType.Timetable` | **Singleton-style**: `Id` is conventionally `null`. Only one timetable in vanilla. `URI()` will emit `tt:` (empty id). |
| `Hyperlink.To(IPlayer)` vs `To(PlayerId)` | Both work. `IPlayer` skips the `NameForPlayerId` lookup (uses `player.Name` directly). Use `PlayerId` only when you don't have the IPlayer handle. |

---

## Patch points for custom EntityType values

A custom entity type requires **5 coordinated patches** (and one optional convenience):

1. **`EntityType` enum extension** — not patchable in C# without IL surgery. Pragmatic alternative: re-use one of the `Unknown`-resolving slots (`Help`/`Crew` in `Text()`, or all-default values) by overlapping numeric, OR ship your own `IModEntityType : int` and translate at the boundaries. Cleaner: just emit the link with a **mod-specific URI scheme** (`mymod:foo`) and patch `LinkDispatcher.Open(string)` ahead of `TryParseURI` — see below.

2. **Easier path: custom URI scheme**
   ```csharp
   [HarmonyPatch(typeof(LinkDispatcher), nameof(LinkDispatcher.Open), new[]{typeof(string)})]
   static class MyLinkPatch {
       static bool Prefix(string link) {
           if (link.StartsWith("mymod:")) { MyDispatcher.Handle(link.Substring(6)); return false; }
           return true;
       }
   }
   ```
   Combined with `new Hyperlink("mymod:foo", "My Foo")` you get full integration without touching the enum. The TMP markup is the same; clicks dispatch to your handler. **The receiver only checks for the literal `<link` substring**, so this works in chat broadcasts, notice content, and `UIPanelBuilder.AddLabel` paths.

3. **If you must extend `EntityType`** (e.g., to ship via ledger or notice): use a high numeric value (>>8), patch all four switches (`URI()`, `TryParseURI`, `Text()`, `LinkDispatcher.Open(EntityReference)`), accept that older-version clients see garbage, and worry about the MessagePack `[Key("type")]` deserializing your int correctly (it should — the enum is `int`-backed).

4. **Custom `Hyperlink.To(MyType)` factory** — ship a static helper class. The struct ctor is public; you don't need to extend `Hyperlink`.

5. **Notice card support**: if you need `EntityReference` to render in `NoticeManager`, your `Text()` patch must resolve a sensible label, and `LinkDispatcher.Open` must do something useful when the entity-prefix link is clicked.

### Init order pitfalls

- `Hyperlink.To(Industry)` reads `industry.identifier` and `industry.name` — fine post-load. Pre-load (no `OpsController`), creating the hyperlink succeeds (it just stores strings) but **clicking it would resolve to "Unknown Industry"** in `Text()`.
- `Hyperlink.To(Car)` reads `car.id` and `car.DisplayName` — `DisplayName` requires the car's `CarDefinition` loaded. Spawn-time hyperlinks may have `null`/empty labels.
- `Hyperlink.To(IPlayer)` works any time; `Hyperlink.To(PlayerId)` requires `StateManager.Shared.PlayersManager` initialized — pre-PlayersManager, `NameForPlayerId` returns null and the label is empty (not "Unknown Player").
- `LinkDispatcher.Open` requires `WindowManager.Shared` to have constructed the relevant windows. **Don't call from Awake.**
- `TextLinkReceiver` requires the host TMP_Text to be on a Canvas with an EventSystem — true for all vanilla UI, false for worldspace TMP without a raycaster.

---

## MP semantics

- **Entity ids are content keys, not handles.** `car.id` (e.g. `"LV-1234"`), `industry.identifier` (e.g. `"alarka"`), `PlayerId.String` (Steam id), `TrainCrew.Id` (Guid string), `PassengerStop.identifier` are all assigned at definition/load time and are stable across save/load and across clients. So a `car:LV-1234` link emitted by the host resolves to the same `Car` on every client.
- **No replication of `Hyperlink`/`EntityReference` per se.** What replicates is the *rendered string* (inside `Alert.Message`, `Say.text`) or the structured form (`SerializableEntityReference` inside `Ledger.Entry.Payee` / `PostNoticeEphemeral.Entity`). The dispatch happens client-locally.
- **Click handlers run on the clicker's machine.** `LinkDispatcher.Open` doesn't send anything across the network — opening the CarInspector, jumping the camera, etc., is purely local.
- **Position links have no MP-stability guarantee** — they're literal world coordinates. Floating-origin re-origin shifts (`WorldTransformer`) can re-base the world post-link-creation; coordinates baked into a link reflect the origin at emit-time on that client. **For host-broadcast links, the host's origin is what's encoded** — clients with a different floating-origin will jump to a different world location. See [`floating-origin.md`](floating-origin.md).
- **Player links resolve via `PlayersManager.PlayerForId`** which reads the client's local roster snapshot. A player who has since disconnected resolves to `null` → "Unknown Player".

---

## Patch surface summary (one table)

| Goal | Hook | File:line |
|---|---|---|
| Add custom URI scheme | `LinkDispatcher.Open(string)` Prefix | `Helpers/LinkDispatcher.cs:20` |
| Add new `EntityType` resolution | `EntityReference.URI()`, `TryParseURI`, `Text()`, `LinkDispatcher.Open(EntityReference)` | per-method |
| Intercept all link clicks | `TextLinkReceiver.OnPointerClick` Postfix or per-receiver `OnLinkClicked` | `UI/TextLinkReceiver.cs:22` |
| Tap chat broadcasts | `Multiplayer.Broadcast(string)` Prefix/Postfix | `Network/Multiplayer.cs:181` |
| Restyle hyperlink markup | `Hyperlink.ToString()` | `Hyperlink.cs:14` |
| Add link receiver to toasts | Postfix `Toast.Present` to `AddComponent<TextLinkReceiver>()` on the Toast singleton's text | `UI.Common/Toast.cs:19` |
| Auto-add receiver more aggressively | `UIPanelBuilder.AddTextLinkReceiverIfNeeded` | `UI.Builder/UIPanelBuilder.cs:234` |
| Revive PassengerStop dispatch | `LinkDispatcher.Open(EntityReference)` case `PassengerStop` | `Helpers/LinkDispatcher.cs:53` |
| Custom click on Car (e.g., disable Shift-select) | `LinkDispatcher.Open(EntityReference)` case `Car` | `Helpers/LinkDispatcher.cs:58` |
| Custom Notice routing | `NoticeManager.PostEphemeralLocal` | `Game.Notices/NoticeManager.cs:66` |

---

## Cross-references

- [Console Commands](console-commands.md) — chat dispatch (`Console.HandleUserInput` → `Say` vs `/cmd`), `Console.Log`, `Console.AddLine` rendering pipeline.
- [Players & TrainCrews](players-traincrew.md) — `PlayerId` semantics, `PlayersManager.NameForPlayerId`, the `RemotePlayer` lookup that powers `Hyperlink.To(IPlayer)`.
- [Economy](economy.md) — `Ledger.Entry.Payee` is the only `EntityReference?` field that goes to disk via `SerializableLedgerEntry`. Every ApplyToBalance call is a candidate emitter.
- [Passengers & Timetable](passengers-timetable.md) — `AutoEngineerPassengerStopper.Say` lines (`To(PassengerStop)` upgrade pattern), `TimetableController.UpdateTimetable` broadcast (`To(EntityType.Timetable, null)`).
- [Floating Origin](floating-origin.md) — Position-link MP stability gotcha.
- [Events Catalog](events-catalog.md) — `BalanceDidChange`, `CarDidDerail`, etc. — Messenger events that don't carry hyperlinks themselves but trigger consumers that emit broadcasts containing them.
