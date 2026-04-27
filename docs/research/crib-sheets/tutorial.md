# Tutorial — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Console Commands](console-commands.md), [State Manager](state-manager.md), [Save & Load](save-load.md), [Progression](progression.md)

The vanilla Railroader tutorial is **a Lua book interpreted at runtime by `InteractiveBookRunner` and rendered by `InteractiveBookWindow`**, with `TutorialManager` as a thin glue layer that owns one KVO object (`"tutorial"`) and decides when to auto-show. There is no compiled chapter/page state machine in C# — chapter advancement, trigger conditions, callout placement, goal tracking, and skip logic all live in `<StreamingAssets>/Tutorial/tutorial.lua` (plus its `?.lua` modules). The C# surface is roughly 100 lines of `TutorialManager`, plus the ~540-line generic `InteractiveBookWindow` that any future scripted help content can reuse. The auto-show gate is a *single* per-scenario boolean (`SetupDescriptor.showTutorial`) plus two persisted KVO keys (`closed`, `complete`); there is no preference, no `[StateRequiredOnLoad]` attribute, no `TutorialFeature` toggle in `GameStorage`. Mods that want to override the tutorial replace the Lua file, replace the KVO ingest, or patch `TutorialManager.Show`.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `UI.Tutorial.TutorialManager` | `UI.Tutorial/TutorialManager.cs` | Owns the `"tutorial"` KVO; decides whether to auto-show on launch. |
| `UI.Tutorial.TutorialManager.Shared` | `UI.Tutorial/TutorialManager.cs:39` | Lazy singleton via `FindObjectOfType` or new GameObject. **Created on first access.** |
| `UI.Tutorial.TutorialManager.Show()` | `UI.Tutorial/TutorialManager.cs:52` | Opens `InteractiveBookWindow` against `StreamingAssets/Tutorial/tutorial.lua`. |
| `UI.Tutorial.TutorialManager.HandleConsoleCommand(string[])` | `UI.Tutorial/TutorialManager.cs:96` | `/tut [chapter [page]]` → writes `chapter_id`/`page_id` KVO keys + `RequestReload`. |
| `UI.Tutorial.TutorialNode` | `UI.Tutorial/TutorialNode.cs` | **Dead code.** ScriptableObject for a node graph that nothing references. |
| `Game.Scripting.Interactive.InteractiveBookWindow` | `Game.Scripting.Interactive/InteractiveBookWindow.cs` | `IPageUI` impl. Generic Lua-driven help-window UI. |
| `Game.Scripting.Interactive.InteractiveBookRunner` | `Game.Scripting.Interactive/InteractiveBookRunner.cs` | Loads `<basePath>/<bookName>.lua`, runs the `run` closure as a Lua coroutine. |
| `Game.Scripting.Interactive.IPageUI` | `Game.Scripting.Interactive/IPageUI.cs` | Lua-callable contract: `say`, `clear`, `start_goal`, `update_goal`, `finish_goal`, `button`, `nav_button`, `add_arrow_overlay`, … |
| `Game.State.StateManager.HasTutorial` | `Game.State/StateManager.cs:181` | `IsHost && SetupDescriptor.showTutorial` — gates auto-show only. |
| `Game.Progression.SetupDescriptor.showTutorial` | `Game.Progression/SetupDescriptor.cs:39` | The per-scenario MonoBehaviour bool that flips tutorials on. |
| `Game.Scripting.ScriptManager` | `Game.Scripting/ScriptManager.cs` | MoonSharp host. `Preset_SoftSandbox` + `LoadMethods`; coroutine-driven `Run`. |
| `Game.Scripting.ScriptWorld` | `Game.Scripting/ScriptWorld.cs` | Static "World" Lua type — sweeping read/write API into game state. |
| `Game.Scripting.ScriptProperties` | `Game.Scripting/ScriptProperties.cs` | KVO accessor wrapped for Lua; supports `observe`. |
| `UI.ArrowOverlayController` | `UI/ArrowOverlayController.cs` | World-space arrow markers used by tutorial callouts. |

---

## Architectural spine

```
SetupDescriptor.showTutorial (scenario MonoBehaviour bool)
        │
        ▼
StateManager.HasTutorial (host-only true if showTutorial)
        │
        ▼
StateManager.OnPropertiesDidRestore (l.321)
        │  if (HasTutorial) TutorialManager.Shared.ShowIfAppropriateForLaunch();
        ▼
TutorialManager.ShowIfAppropriateForLaunch
        │  if (!PlayerClosed && !Complete) Show();
        ▼
TutorialManager.Show
        │  guards on legacy "stack" key (modal alert + bail)
        │  PlayerClosed = false
        │  InteractiveBookWindow.Shared.Show("Tutorial", "tutorial", _keyValueObject)
        ▼
InteractiveBookWindow.Show
        │  _runner = AddComponent<InteractiveBookRunner>()
        │  _window.ShowWindow()
        │  ConfigureKeyValueObject() — observes "complete" → CloseWindow on true
        │  basePath = Path.Combine(Application.streamingAssetsPath, "Tutorial")
        │  _runner.Open(basePath, "tutorial", this, _keyValueObject)
        ▼
InteractiveBookRunner.Open → PostOpen → TryLoadFromFile → StopStart → RunBook
        │  _script = new ScriptManager(null, this, [basePath/?.lua, BuiltInModulesPath])
        │  Lua module returns { title, close_message, extension_type, run = function(ctx) … end }
        │  RunBook coroutine: WaitForStartup (player not in air), then yield return _script.Run(_runClosure, BookContext{ui, properties, world, request_rerun, mark_complete})
        ▼
Lua tutorial.lua: drives ui.say / ui.button / ui.start_goal / ui.add_arrow_overlay / world.* / properties[…] = …
                  reads chapter_id/page_id from properties (set by /tut)
                  calls mark_complete() to set tutorial.complete = true
                  yield numbers to wait, yield nothing to next tick (see ScriptManager.Run l.161)
```

**Take-aways:**

1. The "chapter / page state machine" is **a Lua script**. Search for it in `Application.streamingAssetsPath/Tutorial/tutorial.lua`. It's the canonical source of truth for what each lesson does. **C# patches alone cannot change tutorial flow** — they can only intercept the launch decision, replace the runner, or shadow the KVO.
2. `chapter_id` / `page_id` are *not* C# concepts. They are KVO keys the C# `HandleConsoleCommand` writes; the Lua script reads them via `properties["chapter_id"]` / `properties["page_id"]` and decides what to render.
3. **Tutorial UI is not bespoke.** It's the same `InteractiveBookWindow` any future scripted help feature will use (`extension_type` field on the Lua return table is the discriminator the engine logs but doesn't dispatch on; mod-side use TBD).
4. **There is no `[StateRequiredOnLoad]` attribute in vanilla.** The cribsheet-survey's references to it (`save-load.md:224` notes `if (HasTutorial)`; `wear-durability.md:343` and `kvo-patterns.md:441` use the phrase as shorthand for "any MonoBehaviour that exists at scene-load") are conceptual, not a real Harmony/Unity attribute. The pattern they describe is: a normal MonoBehaviour placed in the in-game scene whose `Awake` runs before `OnPropertiesDidRestore`. `TutorialManager` itself is created lazily by `FindObjectOfType` or `new GameObject` (l.45) — there is no scene-placed prefab; it's spawned on demand.

---

## `UI.Tutorial.TutorialManager`

A 109-line MonoBehaviour. Owns one KVO object and a window reference.

### Constants and KVO keys

```csharp
private const string ObjectId      = "tutorial";   // KVO object id
private const string KeyClosed     = "closed";     // KeyClosed const not actually referenced by name; literal "closed"
private const string KeyComplete   = "complete";
```

(`UI.Tutorial/TutorialManager.cs:13-17`.)

### KVO key map

| Key | Type | Reader/writer | Purpose |
|---|---|---|---|
| `closed` | bool (or null) | C# `PlayerClosed` getter/setter; `null` means false | Player dismissed the tutorial window — suppresses auto-show |
| `complete` | bool | C# `Complete` getter; **Lua sets via `mark_complete()`** (`InteractiveBookRunner.MarkComplete`) | Tutorial finished — suppresses auto-show, hides toolbar button, auto-closes window |
| `stack` | (legacy, unused) | C# `Show` checks `IsNull` | Pre-2025.1 tutorial format marker. If non-null on load, modal alerts the player and bails (`TutorialManager.Show l.54-58`). |
| `chapter_id` | string | written by C# `HandleConsoleCommand`; read by Lua | Jump to chapter — `/tut <chapter>` |
| `page_id` | string | written by C# `HandleConsoleCommand`; read by Lua | Jump to page — `/tut <chapter> <page>` |
| (other keys) | any | Lua-defined via `properties[…] =` and `properties.observe` | Tutorial state — chapter progress, goal completion, branching decisions, sub-page state |

**Important:** the KVO is a generic property bag. Any key the Lua script writes is persisted via the `tutorial` snapshot — including arbitrary state for resume-after-save. There is **no schema validation**; mod-replaced tutorials can use whatever keys they want.

### Lifecycle

```csharp
private void Awake()                                      // l.82
{
    _keyValueObject = base.gameObject.AddComponent<KeyValueObject>();
    StateManager.Shared.RegisterPropertyObject(
        "tutorial", _keyValueObject, AuthorizationRequirement.HostOnly);
}
private void OnDestroy()                                  // l.88
{
    if (StateManager.Shared != null)
        StateManager.Shared.UnregisterPropertyObject("tutorial");
}
```

- **`AuthorizationRequirement.HostOnly`** at register-time means *every* key on the `"tutorial"` object is HostOnly — clients cannot write any tutorial state. See [State Manager § auth resolver](state-manager.md#auth-resolver).
- **Singleton creation:**
  ```csharp
  public static TutorialManager Shared
  {
      get
      {
          if (_shared == null) {
              _shared = Object.FindObjectOfType<TutorialManager>()
                        ?? new GameObject("TutorialManager").AddComponent<TutorialManager>();
              _shared.gameObject.hideFlags = HideFlags.DontSave;
          }
          return _shared;
      }
  }
  ```
  (`l.39-50`.) **First access creates the GameObject if no scene instance exists.** Mods should not call `TutorialManager.Shared` early in init unless they want to be the ones who construct it (and thus register the `"tutorial"` KVO before snapshot ingest — see [Init order gotchas](#init-order-and-gotchas)).
- `HideFlags.DontSave` — the GameObject is excluded from Unity's scene serialization. Reasonable, since the persisted state is the KVO snapshot.

### `Show()` (l.52)

```csharp
public void Show()
{
    if (!_keyValueObject["stack"].IsNull) {
        _keyValueObject["stack"] = null;
        ModalAlertController.PresentOkay("The tutorial has changed!", "...In Railroader 2025.1 the tutorial was revamped...");
        return;
    }
    InteractiveBookWindow shared = InteractiveBookWindow.Shared;
    if (!shared.IsShown) {
        shared.OnPlayerClosed = delegate { PlayerClosed = true; };
        shared.Show("Tutorial", "tutorial", _keyValueObject);
    }
    _window = shared;
    PlayerClosed = false;
}
```

- **Legacy save migration:** the *only* migration. If a save predates the 2025.1 revamp, the `stack` key will be set; the manager pops a modal and refuses to open the new tutorial. The `stack` key is then cleared (so the modal only shows once).
- **`OnPlayerClosed` is reassigned every Show call.** Single-cast Action — if a mod patches `Show` to add another close listener, it must use `+=` or wrap.
- `PlayerClosed = false` runs **after** opening the window, so the auto-show gate resets even if the user immediately re-closes (the next close will set it back to true via `OnPlayerClosed`).

### `ShowIfAppropriateForLaunch()` (l.73)

```csharp
public void ShowIfAppropriateForLaunch()
{
    Log.Information("TutorialManager.ShowIfAppropriateForLaunch playerClosed={playerClosed}, complete={complete}",
                    PlayerClosed, Complete);
    if (!PlayerClosed && !Complete) Show();
}
```

Called once from `StateManager.OnPropertiesDidRestore` (`StateManager.cs:321-324`) **only on the host** (gated by `HasTutorial`, which short-circuits to false off-host). Clients never auto-show.

### `HandleConsoleCommand(string[])` (l.96)

```csharp
public void HandleConsoleCommand(string[] arguments)
{
    Show();                                                // ensure window is open
    if (arguments.Length != 0) {
        _keyValueObject["chapter_id"] = arguments[0];
        if (arguments.Length > 1)
            _keyValueObject["page_id"] = arguments[1];
        _window.RequestReload();                           // re-run the Lua book
    }
}
```

Invoked by `ConsoleCommandHandler._HandleSlashCommand` for both `/tut` and `/tutorial` (`UI.Console/ConsoleCommandHandler.cs:209-212`). See [Console Commands § hand-rolled switch](console-commands.md#hand-rolled-in-_handleslashcommand-switch-bypasses-both-registries).

**Important behaviours:**
- `/tut` with no args → just opens the window (no `RequestReload`, so script keeps current chapter).
- `/tut foo` → sets `chapter_id="foo"` AND triggers full Lua reload via `_runner.Reload()` → `RunBook` from the top.
- `/tut foo bar` → sets both keys, reloads.
- **Args are passed as raw strings** — chapter/page IDs are whatever the Lua script expects. Vanilla console tokenizer doesn't quote-strip beyond standard handling (see [Console § tokenization](console-commands.md#tokenization)).
- **Not host-gated** — a client can run `/tut`, the `Show` call opens the window, and then writing `chapter_id` to the HostOnly KVO triggers an auth rejection (the host snaps the value back). The client's window may flash a stale chapter for a frame. Net effect: client `/tut` doesn't usefully re-target the chapter, but it does open the window locally — except `_keyValueObject` is the host-authoritative one and contains no `chapter_id`/`page_id` until host writes it; the Lua book opens fine but at the host's current chapter. This is "works on the local machine but not the way you'd expect" territory.
- **`_window.RequestReload()` will NPE if `Show()` failed** (e.g., legacy `stack` key triggered the modal-then-bail path). `Show` returns early without setting `_window`; the next line dereferences `_window`. If `Show` was previously successful in this session, `_window` is set from that prior call and reload works. **Cold-start `/tut foo` on a legacy save throws.**

### Patch candidates

| Method | Why patch |
|---|---|
| `TutorialManager.Show` | Intercept all show paths (auto-show + console + toolbar button). Prefix to veto, postfix to seed your own KVO keys. |
| `TutorialManager.ShowIfAppropriateForLaunch` | Replace the auto-show decision (e.g., always-show, never-show, mod-defined gate). |
| `TutorialManager.Awake` | Postfix to register additional observers on the `tutorial` KVO before any other code reads/writes it. |
| `TutorialManager.HandleConsoleCommand` | Intercept `/tut`/`/tutorial` (e.g., to add a `--reset` subcommand). |
| `StateManager.HasTutorial` (getter) | Force-enable tutorials on saves whose SetupDescriptor lacks `showTutorial=true` (mod-driven onboarding). Returns false off-host — patching here doesn't make clients auto-show. |
| `InteractiveBookWindow.Show` | Catch the actual book-open path. Useful if you ship a *different* book that re-uses this window. |
| `InteractiveBookRunner.Open` / `TryLoadFromFile` | Redirect the Lua source (e.g., load from a mod folder instead of `StreamingAssets/Tutorial/`). Easier than replacing the file in StreamingAssets. |
| `InteractiveBookRunner.MarkComplete` | Intercept tutorial completion (e.g., grant a one-time bonus, fire mod telemetry). |

### MP authority

- The `"tutorial"` KVO is registered with `AuthorizationRequirement.HostOnly` (l.85). Every key write rejects on clients; host's correction PropertyChange snaps state back. See [State Manager § HostHandlePropertyChangeRejected](state-manager.md#hosthandlepropertychangerejected--the-correction-loop).
- **Auto-show is host-only** by virtue of `HasTutorial`'s `if (!IsHost) return false;` guard.
- **`Show()` itself runs locally on any machine** — clients can press the toolbar button (`ClickedTutorial`) and the window opens. They see whatever chapter the host's KVO is on. The window scrolls/displays correctly because the Lua coroutine runs locally on each client (the script is stateless except for KVO reads).
- **Wait — the runner runs Lua on every client?** Yes. `InteractiveBookRunner.Open` is invoked by every machine that opens the window. The Lua script has full access to `ScriptWorld` (which can call `StateManager.ApplyLocal(new PlaceTrain(...))`, `MapFeatureManager.SetFeatureEnabled(...)`, etc.) — these calls are then auth-gated by the receiving message's own attributes. Client-side Lua issuing `place_train` will hit `StateManager.AssertIsHost()` inside `ScriptWorld.place_train` and throw a `ScriptRuntimeException` (l.311). **Tutorial scripts that mutate world state will only succeed on the host machine, even though the script is running on all machines that have the window open.**

### Related Messenger / KVO events

| Event | Type | Where |
|---|---|---|
| KVO `tutorial.complete` | bool, observed via `Observe("complete", …)` in `InteractiveBookWindow.ConfigureKeyValueObject` (l.133) | Set by Lua via `mark_complete()`; closes window automatically |
| KVO `tutorial.closed` | bool, set by C# `OnPlayerClosed` callback in `Show()` | Suppresses auto-show next launch |
| KVO `tutorial.chapter_id` / `page_id` | strings, set by `/tut` console | Lua reads to jump |
| `PropertiesDidRestore` Messenger event | from `StateManager.OnPropertiesDidRestore` | Triggers `ShowIfAppropriateForLaunch` |

There is **no `TutorialDidChange`, `TutorialStarted`, or `TutorialCompleted` Messenger event in vanilla.** Mods needing post-completion hooks must observe `tutorial.complete` directly via KVO or patch `InteractiveBookRunner.MarkComplete`.

---

## `UI.Tutorial.TutorialNode` — dead code

```csharp
[CreateAssetMenu(fileName = "Tutorial Node", menuName = "Railroader/Tutorial Node", order = 0)]
public class TutorialNode : ScriptableObject
{
    [Serializable] public struct Link { public string text; public TutorialNode target; }
    public string title;
    [TextArea(5, 50)] public string text;
    public Link[] links;
}
```

A ScriptableObject defining a node-graph dialogue tree (title, text, links to other nodes). **Nothing references it.** No type in Assembly-CSharp loads `TutorialNode` ScriptableObjects, no `LoadAll<TutorialNode>` call exists. The `[CreateAssetMenu]` lets developers right-click create them, but the runtime ignores them entirely. Either:
- Vestigial from an earlier tutorial design (pre-Lua), or
- Reserved for a future feature.

**Modders should not build atop `TutorialNode`** — the engine won't pick it up.

---

## `Game.Scripting.Interactive.InteractiveBookWindow`

The actual UI surface. A `Window` MonoBehaviour, generic enough to host any Lua-driven help content. **Don't think of this as "tutorial-specific" — `TutorialManager` is one consumer among potentially many.**

### Public Lua API (`IPageUI`)

| Method | Effect | Element type stored |
|---|---|---|
| `say(text)` | Append a label paragraph | `ElementSay { Text }` |
| `clear()` | Wipe all elements + nav buttons | (clears `_elements` + `_navButtons`) |
| `start_goal(title, message, style) → int` | Append a goal tracker; `style ∈ {"percent","boolean"}` (anything else ⇒ Boolean) | `ElementGoal { Title, Message, Value=0, Style, CustomDisplay=null }` — returns its goal index |
| `update_goal(goalId, value, customDisplay)` | Updates the i-th goal. value can be float/double/int/bool (others throw). Mutates only on actual change (delta > 0.01 or 0→1 transition or customDisplay change) | mutate-in-place |
| `finish_goal(goalId)` | Shortcut for `update_goal(goalId, 1f, null)` | — |
| `reload_button()` | Show a "Reload" button in bottom bar (debug for content authors) | `_showReloadButton = true` |
| `button(text, closure)` | Append an inline action button | `ElementButton { Text, Closure }` |
| `nav_button(text, closure)` | Append a bottom-bar nav button | `_navButtons.Add(ElementButton)` |
| `remove_last()` | Pop the last appended element | — |
| `add_arrow_overlay(locator, hexColor) → int` | Drop a 3D world-space arrow. `locator` may be `ScriptLocation` or a `Vector3` table. Returns arrow id. Tracks for cleanup. | `_arrowOverlayIds.Add(id)` |
| `remove_arrow_overlay(arrowId)` | Hide one arrow | — |

**Element rendering:**

```
[scroll view]
  for element in _elements:
    ElementSay   → AddLabel(text after RemovingLeadingWhitespaceFromLines + ToTMPMarkup)
    ElementButton → AddButton(text, () => InteractiveBookRunner.TryRun(closure, text))
    ElementGoal  → "<style=Goal_Complete|Goal_Open>{title} {percentDisplay}\n{message}</style>"
[hr]
[hstack: bottom bar]
  if _showReloadButton: button "Reload" → _runner.Reload(); Reload();
  spacer
  for navButton in _navButtons: button(text) → TryRun(closure)
```

`PrepareStringForDisplay` runs `RemovingLeadingWhitespaceFromLines().ToTMPMarkup()` — strips leading whitespace per line then converts the in-house Markroader markdown to TMP markup. **TMP rich-text injection vector** — Lua scripts can construct any TMP markup via `say`, since `ToTMPMarkup` doesn't escape unmatched tags.

**Goal styling:**
- Percent: `<style="Goal_Open">{title} {round(value*100)}%</style>` until value > 0.999, then `Goal_Complete`.
- Boolean: no inline value display; complete when value > 0.5.
- `customDisplay` overrides the `{percent}` portion if non-empty.
- Goal completion is visual only — the script must check `properties[...]` or its own state for branching.

### Lifecycle (`Show`/`Awake`/`OnDisable`)

```csharp
public void Show(string directoryName, string bookName, IKeyValueObject keyValueObject)  // l.105
{
    _runner = base.gameObject.GetComponent<InteractiveBookRunner>()
              ?? base.gameObject.AddComponent<InteractiveBookRunner>();
    _runner.OnWillReload -= HandleRunnerWillReload;
    _runner.OnWillReload += HandleRunnerWillReload;
    _keyValueObject = keyValueObject;
    RemoveAllArrows();
    Populate();                                            // creates UIPanel + Build()
    _window.ShowWindow();
    ConfigureKeyValueObject();                             // observes "complete" → CloseWindow
    string basePath = Path.Combine(Application.streamingAssetsPath, directoryName);
    if (!_runner.Open(basePath, bookName, this, _keyValueObject)) {
        ModalAlertController.PresentOkay("Error opening " + bookName, "Please submit a bug report ...");
        return;
    }
    _window.Title = _runner.BookTitle ?? "Book";
    if (_rebuildCoroutine != null) { StopCoroutine(_rebuildCoroutine); _rebuildCoroutine = null; }
}
```

- **Path resolution:** `Path.Combine(Application.streamingAssetsPath, "Tutorial")` for the tutorial-manager call (TutorialManager passes `directoryName="Tutorial"`, `bookName="tutorial"`). Resolves to `Railroader_Data/StreamingAssets/Tutorial/tutorial.lua`. Module path also includes `<basePath>/?.lua` for Lua `require` lookups, plus `ScriptManager.BuiltInModulesPath` (`StreamingAssets/LuaModules/?.lua`).
- **`ConfigureKeyValueObject`** auto-disposes any prior `_completeObserver`, then subscribes to `complete` with `callInitial: false`. **If the tutorial is already complete when `Show` is called, the window will NOT auto-close immediately** — only a *change* to true will. Auto-show paths are guarded by `ShowIfAppropriateForLaunch` (which checks `Complete` first), so this is fine; manual `Show` from the toolbar button or `/tut` re-opens a completed tutorial deliberately.
- **`RefreshLoop`** (started on `OnShownDidChange(true)`) polls every 1s for file modification time via `_runner.ReloadIfModified()`. **Live-reload for content authors.** In a release build with read-only StreamingAssets this never triggers; on dev machines it lets authors edit `tutorial.lua` and see changes without restart.
- **`HandleRequestCloseWindow`** (l.197): every close attempt pops a confirmation modal ("Cancel" / "Close"). Only on confirm:
  - `_runner.Close()` (stops the book coroutine),
  - `OnPlayerClosed?.Invoke()` (TutorialManager sets `closed = true`),
  - `RemoveAllArrows()`,
  - `_window.CloseWindow()`.
- `_pendingCloseRequest` flag debounces re-entry — the modal can be triggered multiple times via window-close button before the first dismisses; later clicks are ignored until the first resolves.

### Patch candidates

| Method | Why patch |
|---|---|
| `InteractiveBookWindow.Show` | The book-open chokepoint. Mod-defined help can either re-use this window with a different `directoryName/bookName` or replace the path resolution. |
| `InteractiveBookWindow.HandleRequestCloseWindow` | Skip the close-confirmation modal (set `_pendingCloseRequest=false` and call `_runner.Close()` + `OnPlayerClosed?.Invoke()` directly). |
| `InteractiveBookWindow.ConfigureKeyValueObject` | Add additional KVO observers (e.g., listen for chapter changes for HUD updates). |
| `IPageUI` methods | Add custom rendering (e.g., embedded video, mini-maps). Subclass `InteractiveBookWindow` and override the `Build` body. |

### Gotchas

- **Window is registered globally via `WindowManager.GetWindow<InteractiveBookWindow>()`** (l.103). Created in `ProgrammaticWindowCreator.Start` (`UI/ProgrammaticWindowCreator.cs:38`). One instance per game session — *not* one-per-book. If a mod tries to open a second book while the tutorial is shown, the running book is replaced (the `_runner.Open` call resets `_basePath`/`_bookName`/`_runClosure` and stops the prior coroutine in `StopStart`).
- **`_completeObserver` is replaced on every `Show` call** — the prior subscription is disposed first, so chained tutorials don't leak observers.
- **Coroutine ownership is on the window MonoBehaviour, not the runner.** `_refreshCoroutine`, `_rebuildCoroutine`, and `_coroutine` (declared but unused?) live on the window. The runner has `_bookCoroutine` for the Lua execution. `OnDisable` stops the refresh loop and disposes the panel; if the window is disabled mid-run, the Lua coroutine continues until `_runner.Close` or `OnDestroy`.
- **`SmoothScrollToNewContent`** auto-scrolls to bottom when the script `say`s new content. If the user has scrolled up to read earlier text, **new `say` calls jerk them back to the bottom**. There's no "user is reading" detection.
- **Goal `update_goal` swallows changes < 0.01.** Authors using a goal as a continuous progress display (e.g., distance covered) won't see updates until they cross a 1% threshold. The 0→1 transition is special-cased.
- **`add_arrow_overlay` requires `Vector3` in *world* space converted from game-space.** `ArrowOverlayController.AddArrow` calls `position.GameToWorld()` internally. `ScriptLocation` paths use `Graph.Shared.GetPositionRotation(...).Position` which returns game-space; the controller re-converts. Custom Lua arrow placement via raw Vector3 tables should pass game-space coords (the converter still runs).
- **All arrow IDs are auto-tracked in `_arrowOverlayIds`.** `RemoveAllArrows` (called on close, on `OnWillReload`, and on `Show`) clears them. Mods that allocate their own arrows via `ArrowOverlayController.Shared.AddArrow` directly bypass this list — harmless, but means tutorial-cleanup doesn't reach mod arrows.

---

## `Game.Scripting.Interactive.InteractiveBookRunner`

Owns the MoonSharp `Script`, a coroutine, and the loaded `run` closure.

### Lua book contract

The Lua module *must* return a table:

```lua
return {
    title         = "...",      -- shown as window title
    close_message = "...",      -- shown in the close-confirmation modal
    extension_type = "...",     -- logged at load; not dispatched on
    run           = function(ctx)
        -- ctx is a Book.Context userdata: { ui, properties, world, request_rerun, mark_complete }
        -- coroutine: yield <number> to wait <number> seconds, yield (anything else) to next frame
        -- nil return → coroutine ends → `Run completed.` log
    end,
}
```

(`InteractiveBookRunner.cs:212-225`.) **Throws if `run` is missing.** No structured error message for missing `title`/`close_message`/`extension_type` — they're optional reads.

`extension_type` is read into a local `text` and logged (`Debug.Log("Loaded book: " + base.name + " with type " + text)`) but never dispatched on. It's metadata; the engine doesn't behave differently per type. Likely intended for future "is this a tutorial / is this a help page / is this an inline scripted event" routing.

### `BookContext` (Lua-visible userdata)

```csharp
[MoonSharpUserData]
private class BookContext {
    public readonly IPageUI ui;                     // → InteractiveBookWindow
    public ScriptProperties properties;             // → tutorial KVO wrapped
    public ScriptWorld world;                       // → ScriptWorld.Shared
    public Action request_rerun;                    // → StopStart (restarts the book from top)
    public Action mark_complete;                    // → MarkComplete (sets tutorial.complete = true)
    public BookContext(IPageUI ui, ScriptProperties properties, ScriptWorld world, Action request_rerun, Action mark_complete) {…}
}
```

(`InteractiveBookRunner.cs:14-35`.) Registered via `UserData.RegisterType<BookContext>(InteropAccessMode.Default, "Book.Context")` in `Awake` (l.64).

**Lua entry-point signature:** `function(ctx) … end` where `ctx.ui`, `ctx.properties`, `ctx.world`, `ctx.request_rerun()`, `ctx.mark_complete()`.

### `RunBook` coroutine

```csharp
private IEnumerator RunBook()
{
    _pageUI.clear();
    yield return WaitForStartup();                      // wait until player.character.IsInAir == false
    Log.Debug("Running...");
    _script.ClearLastRunError();
    BookContext bookContext = new BookContext(_pageUI, new ScriptProperties(_keyValueObject, _script),
                                              ScriptWorld.Shared, StopStart, MarkComplete);
    yield return _script.Run(_runClosure, bookContext);
    Log.Debug("Run completed.");
    if (_script.LastRunError.HasValue) {
        // present error inline + reload_button()
    }
    _bookCoroutine = null;
}

private static IEnumerator WaitForStartup()
{
    PlayerController playerController = CameraSelector.shared.character;
    while (playerController.character.IsInAir)
        yield return new WaitForSeconds(1f);
}
```

**`WaitForStartup` is a hard-coded gate**: until the player character is on the ground, the script doesn't run. This protects against running tutorial logic before the world finishes settling (player is dropped from spawn). **No timeout.** If the player character never grounds (e.g., spawn point in mid-air over invalid terrain), the tutorial silently never starts.

### `Reload()` and `ReloadIfModified()`

- `Reload()` fires `OnWillReload` (which `InteractiveBookWindow` uses to remove arrows), stops the coroutine, disposes the script, re-runs `PostOpen` (re-reads file, rebuilds script, restarts from top).
- `ReloadIfModified()` checks `File.GetLastWriteTime` against `_lastModifiedTime` cached on last load. Window's `RefreshLoop` polls this every 1s.

### `MarkComplete`

```csharp
private void MarkComplete()
{
    _keyValueObject["complete"] = true;                // bool, HostOnly
}
```

Called from Lua via `ctx.mark_complete()`. Triggers `InteractiveBookWindow._completeObserver` → `_window.CloseWindow()`. **Will fail silently on clients** (HostOnly write rejected) — clients running tutorial Lua locally cannot mark themselves complete.

### Patch candidates

| Method | Why patch |
|---|---|
| `InteractiveBookRunner.Open` | Substitute the file source (e.g., load from a mod folder; load from memory instead of disk). |
| `InteractiveBookRunner.TryLoadFromFile` | Hijack the parse/load of the Lua book — inject your own `BookContext` extensions, swap `_runClosure`, etc. |
| `InteractiveBookRunner.MarkComplete` | Add side-effects on tutorial completion (achievement, telemetry, unlock). |
| `InteractiveBookRunner.RunBook` | Skip `WaitForStartup`, change error presentation. |

### Gotchas

- **`ScriptManager` is created with `_player = null`** when invoked by the runner (`new ScriptManager(null, this, modulePaths)` at `InteractiveBookRunner.cs:199`). The player param is unused for tutorial books but reserved for future per-player scoping.
- **Module path includes `LoadMethods`** (`ScriptManager.Reset` l.179: `coreModules |= CoreModules.LoadMethods` if `_scriptLoader != null`). This means `require` works in tutorial scripts. Without it, `require` would error.
- **`_currentScript` is a static** (`ScriptManager.cs:33`) used by `ScriptManager.CurrentScript`. Concurrency safety relies on `RunBook`/`Run` being a Unity coroutine on the main thread. Mods running another script in parallel could race on this static.
- **`LastRunError` persists across runs.** `Reload` calls `ClearLastRunError` first. If a mod queries `LastRunError` while polling, account for stale values.
- **`StopStart` (a.k.a. `request_rerun`) restarts the script from the top, NOT from the current chapter.** The Lua script must read `properties["chapter_id"]` to resume at a chapter. If the script doesn't honor `chapter_id`, `request_rerun` always restarts at chapter 1.
- **Lua can store anything in `properties`**, but only types `Value` supports — Nil, Bool, Int (ChangeType from Lua number when integral), Float (Lua number with fraction), String. Tables are NOT supported (`ScriptProperties.ToValue` l.83 throws `NotImplementedException` for `DataType.Table`). **Tutorial state cannot persist nested objects directly** — must serialize to string or split into multiple keys.
- **`Closure` (function) values from Lua passed via `ui.button(text, closure)`** are held in `ElementButton.Closure`. When the user clicks, `InteractiveBookRunner.TryRun(closure, text)` calls `closure.Call()` — but **this is OUTSIDE the `RunBook` coroutine**. `_currentScript` may be null at that point. If the closure needs `ScriptManager.CurrentScript`, it'll throw. Vanilla closures stored in buttons are typically simple state-mutators (write a property, call `request_rerun`), so this hasn't bitten. Mod authors writing complex button callbacks should be aware.
- **`TryRun` swallows all exceptions** — Syntax + Runtime + generic. Returns `true` on either success OR runtime/syntax error (only generic `Exception` returns `false`). The Lua error is logged but the user sees nothing. **Buttons that silently no-op are usually a Lua error.** Check the log.

---

## `Game.Scripting.ScriptWorld` — what tutorials can do

Static class registered as Lua type `"World"`. Exhaustive surface (`Game.Scripting/ScriptWorld.cs`):

| Method | Purpose | MP authority |
|---|---|---|
| `time`, `timeScale` | Real time / Unity timeScale | Local |
| `say(message)` | `Multiplayer.Broadcast(message)` — chat | Network broadcast |
| `set_feature_enabled(feature, enabled)` | `MapFeatureManager.SetFeatureEnabled` | HostOnly via mapFeatures KVO |
| `set_property(objectId, key, value)` | Generic KVO write via `StateManager.ApplyLocal(new PropertyChange(...))` | Auth-gated per object/key |
| `get_property(objectId, key)` | Read any KVO key | Local read |
| `set_signal_system("off"|"ctc"|"abs")` | Reconfigure signals + CTC mode | HostOnly mapFeatures + ctc.mode |
| `code_ctc_route(interlockingId, nr, lr)` | `CTCPanelController.CodeSwitchAndSignal` | Local-ish (assumes CTC panel) |
| `is_block_occupied(blockId)` | CTC block query | Local |
| `reset()` | Wipe everything (all cars, switches, mapFeatures, notices, CTC, passenger waiting) | Host-side cascade — destructive |
| `get_distance(a, b)` | Graph route distance | Local read |
| `car_at_location(loc, radius=1)`, `find_car_from_location(loc, distance, except)` | Lookup cars by track location | Local |
| `jumpToIndustry(id)`, `jump_to_position(table)`, `orient_toward(table)` | Camera moves | Local camera |
| `selected_camera()` | "overhead"/"first-person" | Local |
| `get_player_position()`, `get_camera_position()`, `get_mouse_look()`, `get_field_of_view()` | Player/camera state | Local |
| `get_cars(typeFilter)`, `get_selected_car()`, `get_inspector_car()`, `get_seated_car()`, `get_seated_locomotive()`, `get_attached_locomotive()`, `get_seated_engineer()` | Car queries | Local read |
| `place_train(location, ids[])`, `place_train_at_interchange(...)` | Spawn cars | **`StateManager.AssertIsHost()` — throws on client** |
| `get_marker_location(markerId)` | TrackMarker lookup | Local |
| `set_switch_thrown(id, thrown)`, `get_switch_thrown(id)`, `get_switch_position(id)` | Switch read/write | Goes through `TrySetSwitch` (auth via `RequestSetSwitch` message) |
| `check_same_route(a, b, limit)` | Graph topology check | Local |
| `get_passenger_stop(id)` | PassengerStop accessor | Local |
| `property_equals(objectId, key, expected)` | Convenience comparison | Local read |
| `get_company_window_path()` | Path of currently shown CompanyWindow tab | Local |
| `get_industry_next_contract_tiers()` | Per-industry next-contract tier dict | Local read (via OpsController) |
| `reset_movement_counter()`, `get_movement_counter("forward"|"back"|"left"|"right")`, `get_movement_jumped()` | Movement input deltas — used to detect "player moved using WASD" | Local input state |

**Trigger-condition primitives are all here.** A "player did X" tutorial chapter typically:
1. `world.reset_movement_counter()` to start a measurement window.
2. `coroutine.yield(0.5)` until `world.get_movement_counter("forward") > someThreshold`.
3. `ctx.mark_complete()` or advance `properties["chapter_id"]`.

For more complex triggers (player coupled cars, opened anglecock, set up a contract):
- Observe KVO directly via `ctx.properties.observe(key, cb)` on the *tutorial* KVO (limited) OR via `world.get_property(carId, "_f.coupled")` polling.
- For property changes on other objects, **there's no Lua API to observe arbitrary KVOs** — only the tutorial's own `properties.observe`. Tutorials must poll.

**Lua call patterns for callouts:**

```lua
-- Goal:
local g = ctx.ui.start_goal("Couple to the boxcar", "Walk to BX 2317 and uncouple it from the consist", "boolean")
-- ... wait until condition ...
ctx.ui.finish_goal(g)

-- Arrow callout in world:
local loc = ctx.world.get_marker_location("yard-east-switch")
local arrow = ctx.ui.add_arrow_overlay(loc, "FFAA00")
-- ... after action ...
ctx.ui.remove_arrow_overlay(arrow)

-- Persist progress:
ctx.properties["chapter_id"] = "coupling"
ctx.properties["page_id"] = "step3"
```

---

## `/tut` and `/tutorial` console interaction

See [Console Commands § hand-rolled](console-commands.md#hand-rolled-in-_handleslashcommand-switch-bypasses-both-registries) for full dispatch context.

| Form | Effect |
|---|---|
| `/tut` | `TutorialManager.Shared.HandleConsoleCommand(new string[0])` → `Show()` only; no reload |
| `/tut chap1` | `Show()`; `properties["chapter_id"] = "chap1"`; `_runner.Reload()` from top |
| `/tut chap1 page2` | `Show()`; sets both keys; reload |
| `/tutorial …` | identical alias |

**No host gate.** Anyone can type. Effects:
- On host: works as expected; KVO write happens locally, broadcasts to clients, Lua reload picks up new chapter.
- On client: `Show()` opens the local window; `properties["chapter_id"] = …` triggers a `PropertyChange` send that the host **rejects** (HostOnly), which sends back a corrective PropertyChange with the host's current value; meanwhile the client's `_runner.Reload()` re-runs the Lua against whatever values are present at reload time. **Net behaviour: client `/tut chap1` opens the window but jumps to whatever chapter the host's `tutorial.chapter_id` is currently set to.** The client's "request to jump" is silently lost.

**Bug mentioned above:** if the legacy `stack` key is set on a save (pre-2025.1 migration), the *first* `Show` call presents the modal and returns without setting `_window`. A subsequent `_window.RequestReload()` from `HandleConsoleCommand`'s arg-handling branch dereferences `_window` (null) → NRE. Workaround: dismiss the modal, then run `/tut foo` a second time.

---

## Save/load: tutorial progress persistence

The `"tutorial"` KVO is registered via `RegisterPropertyObject` (l.85). It participates in the standard snapshot path:

- **Save:** `StateManager.PopulateSnapshotForSave` → `_propertyObjectManager.PopulateSnapshotForSave` walks every registered object. Tutorial state is in `Snapshot.Properties["tutorial"]` as a dictionary of all keys ever written (`closed`, `complete`, `chapter_id`, `page_id`, plus any keys the Lua script wrote via `properties[...] = ...`).
- **Load:** `PopulateFromRemoteSnapshot` → `RestoreProperties` populates the KVO with `Local` origin (host) or `Remote` (client). **TutorialManager.Awake's `RegisterPropertyObject` must run before snapshot ingest** — if it doesn't, the snapshot's tutorial data lands in `PropertyObjectManager._records["tutorial"]` as a synthetic `KeyValueStorage`, and is replayed when `TutorialManager.Awake` finally registers (via `KeyValueObject.ResetData(oldData, restoreOrigin)`). See [State Manager § mod state in save](state-manager.md#mod-state-in-save).
- **Per-save, host-authoritative.** Multi-player clients see the host's tutorial state. A client joining a save mid-tutorial can open the window (toolbar button) and see the current chapter. The same snapshot is sent to every connecting client.
- **Per-player tutorial progress: not vanilla.** All players share one tutorial state. If two players join a fresh tutorial save, both see the same progress. Mods that want per-player tutorials need to: register a separate KVO per player id, or use `PlayerProperties` (the PlayersManager's per-player KVO bag — see [Players & TrainCrew](players-traincrew.md)).
- **No tutorial-specific save migration**, except the `stack` legacy-key check in `Show` which warns and bails for pre-2025.1 saves.

---

## Skipping / dismissing tutorials

Three orthogonal mechanisms:

1. **Player closes the window** → `InteractiveBookWindow.HandleRequestCloseWindow` → confirmation modal → on confirm: `OnPlayerClosed?.Invoke()` → `TutorialManager`'s `closed = true` → suppresses auto-show until next launch / next save load. Toolbar button (`tutorialButton`) remains visible because `Complete` is false. User can re-open via the toolbar at any time.
2. **Lua script calls `mark_complete()`** → `tutorial.complete = true` → `InteractiveBookWindow._completeObserver` triggers → `_window.CloseWindow()` (skips the close-confirmation modal). `TopRightArea.HandlePropertiesDidRestore` will hide the toolbar button on next restore (the live observer doesn't update visibility — only on `PropertiesDidRestore` Messenger event, so the button stays visible until next load). **The button visibility is stale until next snapshot restore.**
3. **Scenario does not enable tutorials** → `SetupDescriptor.showTutorial = false` → `HasTutorial` is false → `ShowIfAppropriateForLaunch` is never called → `tutorialButton` is hidden. User can still type `/tut` to force-open.

There is **no in-game "Skip Tutorial" button** in vanilla. The close-confirmation modal is the closest thing.

To programmatically skip in a mod:

```csharp
// Direct approach (host only):
TutorialManager.Shared.GetType()
    .GetField("_keyValueObject", BindingFlags.NonPublic | BindingFlags.Instance)
    .GetValue(TutorialManager.Shared) as IKeyValueObject;
kvo["complete"] = true;

// Cleaner: write via PropertyChange
StateManager.ApplyLocal(new PropertyChange("tutorial", "complete", new BoolPropertyValue(true)));
```

---

## Sandbox / MP-host / MP-client visibility matrix

| Behaviour | Sandbox (SP) | MP-Host | MP-Client |
|---|---|---|---|
| `HasTutorial` returns true | If save's `SetupDescriptor.showTutorial` | Same | **Always false** (`!IsHost` early-return) |
| Auto-show on launch | If `HasTutorial && !PlayerClosed && !Complete` | Same | Never (gated by HasTutorial) |
| Toolbar tutorial button visible | If `HasTutorial && !Complete` | Same | Never (HasTutorial false off-host) |
| `/tut` / `/tutorial` available | Yes | Yes | Yes — but `Show()` opens the window without auto-show ever having fired; KVO writes get rejected |
| Tutorial Lua runs locally | Yes | Yes | Yes (when window is open) |
| `mark_complete()` succeeds | Yes | Yes | No (HostOnly KVO rejects) |
| `world.place_train` / Lua-driven world mutations | Yes | Yes | No (`AssertIsHost` throws) |
| Save persists tutorial state | Yes | Yes | N/A (clients don't save) |

**Key callout:** in MP, only the host has a "real" tutorial. Clients can manually open `InteractiveBookWindow` (via `/tut`) and read the host's current tutorial chapter, but they cannot advance it, complete it, or get auto-show on join. In practice the tutorial is a single-player feature that happens to render on all machines if anyone opens the window.

---

## Patch points for custom tutorials, intercept, and hide

### Adding a custom tutorial without replacing the vanilla one

**Approach 1: Add a parallel scripted help book.**
- Create a Lua book at `<StreamingAssets>/<MyHelpDir>/<myhelp>.lua` (or via `Open` patching, anywhere on disk).
- In your mod, call `InteractiveBookWindow.Shared.Show("MyHelpDir", "myhelp", myKeyValueObject)`. Provide a KVO you register yourself (your mod's `MyHelpManager.Awake` calls `RegisterPropertyObject("myhelp", _kvo, AuthorizationRequirement.HostOnly)`).
- Note: only one `InteractiveBookWindow` instance exists; opening yours while the tutorial is open will replace the running book. Wrap `Show` to check `_window.IsShown` first if you want to be polite.

**Approach 2: Build your own UI.**
- The `InteractiveBookWindow`/`InteractiveBookRunner` pair is reusable on its own GameObject. Instantiate a second `Window` + add the components, register a different KVO. (The vanilla window registers via `WindowManager.GetWindow<InteractiveBookWindow>()` which returns the singleton — you'd need to subclass to get a second registered type.)

**Approach 3: Replace `tutorial.lua` outright.**
- Drop a replacement at `<StreamingAssets>/Tutorial/tutorial.lua`. Vanilla doesn't checksum or version-check the file. Original tutorial is gone.
- Bonus: live-reload via `_runner.ReloadIfModified` works in dev builds (1-second polling) — you can iterate the script without restarting the game.

### Intercepting tutorial advancement

- **Patch `InteractiveBookRunner.MarkComplete`** for completion side-effects.
- **Patch `KeyValueObject.SetValue` on the tutorial KVO** by subscribing via `_keyValueObject.Observe(key, ...)` from your mod's MonoBehaviour after `TutorialManager.Awake` has registered the object. Use `PropertiesDidRestore` to time the subscription.
- **Patch `InteractiveBookRunner.TryRun`** to hook every Lua-driven button activation.
- **Patch `IPageUI.start_goal` / `update_goal` / `finish_goal`** (instance methods on `InteractiveBookWindow`) to capture goal-tracking events for HUD overlays or progress export.

### Hiding the vanilla tutorial in mod-driven flows

- **Patch `StateManager.HasTutorial` getter** to return false → suppresses auto-show + hides toolbar button.
- **Set `tutorial.complete = true` in your mod's init** → both auto-show and the toolbar button become inert. The user can still type `/tut` (which always force-opens). To block `/tut`, patch `TutorialManager.HandleConsoleCommand` prefix-return-false.
- **Patch `TopRightArea.HandlePropertiesDidRestore`** prefix to force `tutorialButton.gameObject.SetActive(false)` independent of `HasTutorial`.
- **Replace `SetupDescriptor.showTutorial` getter via Harmony** if you want per-scenario control without modifying scene assets. (Field, not property — Harmony patches on field access don't work; you'd have to patch every reader, of which there are two: `StateManager.HasTutorial` and the field's getter at JIT time. Easier to patch `HasTutorial`.)

### Adding mod-side trigger conditions

The Lua surface (`ScriptWorld`) covers most game-state queries but lacks observers for arbitrary KVOs and lacks Messenger access. To add e.g. "advance when player fires `MyModEvent`":
- Add a Messenger.Default observer in your mod that writes a key onto the `tutorial` KVO when the event fires (host only — KVO is HostOnly).
- The Lua can then poll or observe that key via `ctx.properties.observe("my-mod-trigger", function(v) ... end)`.

For more elaborate Lua extensions, register additional Lua types via `UserData.RegisterType<T>` — but this needs to happen before `ScriptManager.StaticInit` (`ScriptManager.cs:192-208`), which runs lazily on first ScriptManager construction and uses `_initialized` to skip. **Patch `ScriptManager.StaticInit` postfix** to register custom types after vanilla; register on `BookContext` itself (subclass via Harmony field-add is impractical — use a global Lua-accessible singleton via `script.Globals["MyApi"] = typeof(MyScriptApi)` patched into `ScriptManager.Reset`).

---

## Init order and gotchas

### When does `TutorialManager.Awake` fire?

There is no scene-placed `TutorialManager`. The instance is created by:

- **`StateManager.OnPropertiesDidRestore`**: if `HasTutorial`, calls `TutorialManager.Shared.ShowIfAppropriateForLaunch()`. Accessing `Shared` runs the lazy initializer → `FindObjectOfType` returns null → `new GameObject("TutorialManager").AddComponent<TutorialManager>()` → **Awake runs synchronously here, registering the `tutorial` KVO**.
- **`TopRightArea.HandlePropertiesDidRestore`** (via `tutorialButton.gameObject.SetActive(StateManager.Shared.HasTutorial && !TutorialManager.Shared.Complete)`) — accesses `TutorialManager.Shared` which can also trigger creation. Order between `StateManager.OnPropertiesDidRestore` and `TopRightArea.HandlePropertiesDidRestore` depends on Messenger registration order (both subscribe to `PropertiesDidRestore`).
- **`/tut` console command** — typing `/tut` accesses `TutorialManager.Shared`, which lazily creates if not yet present.
- **Toolbar button click** — `TopRightArea.ClickedTutorial()`.

**Critical:** because `RegisterPropertyObject` runs during `Awake`, and `Awake` runs at the moment `TutorialManager.Shared` is first accessed (i.e., during `OnPropertiesDidRestore`), **the registration occurs AFTER the snapshot's `RestoreProperties` has already run**. The snapshot's `tutorial` data therefore lands in the `PropertyObjectManager`'s synthetic `KeyValueStorage` per the late-registration replay path (`PropertyObjectManager.cs:31`'s `keyValueObject.ResetData(oldData, restoreOrigin)`). **This works**, but means the KVO observers in `InteractiveBookWindow.ConfigureKeyValueObject` see "live" values, not "restored values fired with `Remote` origin". It's a subtle ordering — if your mod patches in observers via `Awake`, they'll see the first write as a normal value-set, not a restore-replay.

### Other gotchas

- **`Shared` getter races with scene unload.** If `TutorialManager.Shared` is accessed during `OnDestroy` of another component while the scene is tearing down, the new GameObject creation may fail or leak. Wrap mod accesses in `if (StateManager.Shared != null)` guards.
- **`HideFlags.DontSave` doesn't survive recompiles in editor.** Devs poking the tutorial in Edit mode may see the GameObject persist across script-reloads.
- **`InteractiveBookWindow.Show` calls `InteractiveBookRunner.Open` synchronously.** The Lua file is read (`File.ReadAllText`) and parsed (`_script.Load`) on the main thread. **A large Lua book causes a frame hitch on first open.** No async path.
- **`WaitForStartup` blocks the script.** Player respawns mid-tutorial with `IsInAir == true` will not pause the script — it's only checked at startup. Mods causing mid-script teleports might leave a silent gap (the script continues but the player isn't where the script expects).
- **`Application.streamingAssetsPath` is a non-writable path on most platforms.** Tutorial state cannot live in StreamingAssets — only the Lua source. State is in the save snapshot.
- **`script.Options.RethrowExceptionNested = true`** (`ScriptManager.cs:196`) means exceptions thrown from C# code into Lua are wrapped with full stack info — useful for debugging but the trace can be deep.
- **MoonSharp's `Preset_SoftSandbox` is permissive.** Tutorial Lua can do file IO via `LoadMethods`, can spin tight loops (no instruction limit set), and can call any registered userdata method. **Do not run untrusted Lua via this surface** — any modder-shipped tutorial replacement has effective full game-state mutation.
- **Goal IDs are array indices**, not stable handles. If the Lua script `clear()`s and re-`start_goal()`s, IDs reset starting at 0. `update_goal(oldId, ...)` after a clear writes to whatever goal happens to occupy that index — silent data corruption if the script doesn't re-fetch goal IDs after every clear.
- **`InteractiveBookWindow.IsShown`** returns `_window.IsShown`. If `_window` is null (component not yet wired in `Awake`), it returns false. Don't depend on it during boot.
- **`OnPlayerClosed` is overwritten on every `Show`.** A mod that subscribes via `+=` will have its delegate replaced. Patch the assignment, or use `Messenger.Default.Send` from inside a `TutorialManager.Show` postfix to fan out a custom event.
- **`/help` does NOT list `/tut` or `/tutorial`.** Both are hand-rolled in `_HandleSlashCommand` switch, so they bypass the registries that `/help` enumerates.
- **The Lua book CAN open windows via `world.*`** but cannot directly access most UI panels. `get_company_window_path()` reads but no `set` exists. Mods extending `ScriptWorld` for tutorial use should add `*_window` setters carefully.

---

## Cross-references

- Console-driven entry into the tutorial: see [Console Commands § hand-rolled](console-commands.md#hand-rolled-in-_handleslashcommand-switch-bypasses-both-registries) and [Console Commands § cross-cutting types referenced](console-commands.md#cross-cutting-types-referenced).
- KVO registration with `AuthorizationRequirement.HostOnly`: see [State Manager § RegisterPropertyObject](state-manager.md#registerpropertyobject--late-registration) and [State Manager § auth resolver](state-manager.md#auth-resolver).
- `PropertiesDidRestore` and the `RestoreNotifier` ordering pattern: see [State Manager § lifecycle](state-manager.md#lifecycle--phases) and the `Critical: phases for mod init` table there.
- Tutorial in the save snapshot: see [Save & Load § snapshot schema](save-load.md) and [State Manager § snapshot](state-manager.md#snapshot--late-join). The mod-state-survives-load-order behaviour described under "Save/load: tutorial progress persistence" above is the same `ResetData` replay path.
- `SetupDescriptor` and the broader scenario-setup model: see [Progression](progression.md) for `setupDescriptor`'s role in `CompanyModeSetup` (initial cars, balance, progression id), and [State Manager § ApplyGameSetup](state-manager.md) for the post-load scenario apply.
- TopRightArea toolbar layout (where the tutorial button lives) and other top-right windows: see UI Vanilla survey (general) — the tutorial button's visibility (`HasTutorial && !Complete`) is the only conditional toolbar button driven by host/scenario state, distinct from `timetableButton` which is driven by the `timetableFeature` KVO.
