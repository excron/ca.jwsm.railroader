# Windows & Builders Framework — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/UI.Common/`, `UI.Builder/`, `UI/ProgrammaticWindowCreator.cs`, `Model/ComponentFactory.cs`, `Model.ComponentBuilders/`)
**Companions:** [UI Vanilla](ui-vanilla.md) · [Cars, Cargo & Loading](cars-cargo.md) · [Car Definitions & Components](car-definitions.md) · [Locomotive Architecture](locomotive-architecture.md) · [Tutorial](tutorial.md) · [Input & Keybinds](input-keybinds.md)

Two unrelated registries that share a name and a directory tree. **Window framework** = `WindowManager` + `Window` + `IBuilderWindow` + `ProgrammaticWindowCreator` + `UIPanel`/`UIPanelBuilder` (the in-house fluent uGUI panel DSL). **Component-builder framework** = `IComponentBuilder` + `[ComponentBuilder]` attribute + `ComponentFactory.PrepareBuildersIfNeeded` (the per-`Component`-subtype factory that turns a JSON `Component` descriptor into MonoBehaviours when a car body loads). The "builder" word is overloaded: a `UIPanelBuilder` is a transient struct that builds widgets into a panel; an `IComponentBuilder` is a singleton factory that constructs MonoBehaviours from a definition. Distinct registries, distinct discovery mechanisms, distinct extension hazards.

[`ui-vanilla.md`](ui-vanilla.md) covers the *concrete* vanilla windows (CarInspector, Preferences, Company, etc.). This sheet is the *framework* companion: how the host shape, the persistence, the registry, the panel DSL, and the per-component factory work — and where the silent extension failures live.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `UI.Common.WindowManager` (singleton `.Shared`) | `UI.Common/WindowManager.cs:12` | Children-as-windows registry; `GetWindow<T>`, `HitTest`, `CloseTopmostWindow`, `Present(Alert)` |
| `UI.Common.Window` (MonoBehaviour) | `UI.Common/Window.cs:13` | Window host: title, drag, resize, content rect, show/hide events |
| `UI.IBuilderWindow` / `UI.IProgrammaticWindow` | `UI/IBuilderWindow.cs`, `IProgrammaticWindow.cs` | The two flavors of self-registering windows |
| `UI.ProgrammaticWindowCreator` | `UI/ProgrammaticWindowCreator.cs:18` | Bootstrap that instantiates every vanilla `IBuilderWindow` at scene `Start` |
| `UI.Common.WindowPersistence` (static) | `UI.Common/WindowPersistence.cs:9` | PlayerPrefs round-trip of position/size/shown state |
| `UI.Builder.UIPanel` | `UI.Builder/UIPanel.cs:11` | Persistent panel state — children, observers, rebuild timer |
| `UI.Builder.UIPanelBuilder` (struct) | `UI.Builder/UIPanelBuilder.cs:19` | Fluent widget DSL handed to a build closure |
| `UI.Builder.UIBuilderAssets` (ScriptableObject) | `UI.Builder/UIBuilderAssets.cs:12` | Single shared prefab catalog injected into every window |
| `UI.Builder.UIState<T>` | `UI.Builder/UIState.cs:1` | Trivial mutable cell to share state across rebuild closures (no reactivity) |
| `UI.Builder.UIPanelBuilder.Frequency` enum | `UIPanelBuilder.cs:21` | `Fast` (0.1s) / `Periodic` (1.0s) for poll-based updaters |
| `Model.IComponentBuilder` | `Model/IComponentBuilder.cs:6` | Per-`Component`-subtype factory contract |
| `Model.ComponentBuilders.ComponentBuilderAttribute` | `Model.ComponentBuilders/ComponentBuilderAttribute.cs:6` | `[ComponentBuilder]` marker — discovery beacon |
| `Model.ComponentFactory.PrepareBuildersIfNeeded` | `Model/ComponentFactory.cs:13` | Lazy attribute scan — **only `typeof(ComponentFactory).Assembly`** |
| `Model.ComponentSetup.Setup` | `Model/ComponentSetup.cs:21` | Per-component GameObject + dispatch wrapper |
| `UI.CarEditor.CarEditorWindow.ConfigureAddComponentDropdown` | `UI.CarEditor/CarEditorWindow.cs:265` | The editor's component-add list — **scans `typeof(ComponentAttribute).Assembly`** |

---

## Spine 1: how a window comes alive

```
Scene boot
   │
   ▼
ProgrammaticWindowCreator.Start()                                  ← UI/ProgrammaticWindowCreator.cs:24
   │  for each registered TWindow:
   │     Window window = Instantiate(windowPrefab, this.transform) ← becomes a child of WindowManager
   │     window.SetInitialPositionSize(id, defaultSize, defaultPos, sizing) ← reads PlayerPrefs
   │     window.name = typeof(TWindow).ToString()
   │     TWindow tw = window.gameObject.AddComponent<TWindow>()    ← AddComponent → Awake fires
   │     tw.BuilderAssets = builderAssets                           ← inject shared catalog
   │     window.CloseWindow()                                       ← hidden by default
   ▼
First call to TWindow.Show() (or .Toggle() — pattern varies per window):
   │  TWindow.Populate():
   │     _panel = UIPanel.Create(_window.contentRectTransform, BuilderAssets, BuildClosure)
   │        new UIPanel(...) → DestroyChildren(), add VerticalLayoutGroup if missing
   │        Rebuild()
   │           DisposeChildren()              ← unregister Messenger handlers, dispose KVO observers
   │           _container.DestroyChildren()    ← raze the GameObject tree
   │           _buildClosure(new UIPanelBuilder(_container, _assets, this))
   │              → AddTitle / AddTabbedPanels / AddField / ...
   │           InvokeOnRebuild() → bubble OnRebuild to parent UIPanel
   │  _window.ShowWindow()
   │     SetShown(true) → ShowCoroutine (immediate)
   │        OnShownWillChange?.Invoke(true)
   │        UpdateForShown()                   ← SetActive(true) on EVERY direct child of Window
   │        OnShownDidChange?.Invoke(true)
   │     ClampToParentBounds()
   │     OrderFront() → SetAsLastSibling
```

**Two registration paths** in `ProgrammaticWindowCreator`:

1. **Static signature**: `CreateWindow<TWindow>(string id, int w, int h, Position pos, Sizing s = default)` where `TWindow : Component, IBuilderWindow`. Caller supplies metadata.
2. **Self-described**: `CreateWindow<TWindow>(Action<TWindow> configure = null)` where `TWindow : Component, IProgrammaticWindow`. Window provides its own `WindowIdentifier`/`DefaultSize`/`DefaultPosition`/`Sizing`.

Static signature has an optional `Action<TWindow> configure` callback that runs after `BuilderAssets` is injected — useful for pre-show wiring.

**Important window-prefab note**: every vanilla window is a runtime `AddComponent<TWindow>` onto an instance of the *same* `windowPrefab` GameObject. `windowPrefab` carries `Window` + `DraggablePanel` + `PanelResizer` + the title bar + the empty `contentRectTransform`. Modders that want vanilla chrome can `Object.Instantiate(WindowManager.Shared.GetComponent<...>().windowPrefab)` — but `windowPrefab` is a `public Window` field on `ProgrammaticWindowCreator`, so the cleanest reach is `FindObjectOfType<ProgrammaticWindowCreator>().windowPrefab`. (Or just register your own `IBuilderWindow` via Harmony postfix on `ProgrammaticWindowCreator.Start`, which is the recommended path.)

### Vanilla `IBuilderWindow` registry (re-stated from ui-vanilla.md, framework view)

| Window | Static or self-described | Identifier | Notes |
|---|---|---|---|
| `CompanyWindow` | Static | `"Company"` | 880×600 fixed |
| `PreferencesWindow` | Static | `"Preferences"` | 400×400 → 600×800 resizable |
| `BindingsWindow` | Static | `"Bindings"` | Hosts `AddInputBindingControl` rows |
| `CarInspector` | Static | `"CarInspector"` | LowerRight default |
| `CarCustomizeWindow` | Static | `"CarCustomize"` | |
| `LostCarPlacerWindow` | Static | `"LostCarPlacer"` | |
| `GuideWindow` | Static | `"Guide"` | 600×400 → 1200×1200 resizable |
| `StationWindow` | Static | `"Station"` | |
| `EquipmentWindow` | Static | `"Equipment"` | |
| `TimeWindow` | **Self-described** | `"Time"` | Implements `IProgrammaticWindow` |
| `TimetableWindow` | **Self-described** | (own) | |
| `TimetableEditorWindow` | **Self-described** | (own) | |
| `InteractiveBookWindow` | **Self-described** | (own) | Tutorial + book runner host |

`ScriptTestsWindow` (Game.Scripting.Testing) implements `IBuilderWindow` but is NOT registered in `ProgrammaticWindowCreator.Start` — it's instantiated separately by `ScriptTestsController` for dev-only test runs. Mods can copy that pattern to host their own builder windows outside the bootstrap registry.

**Other windows that bypass `ProgrammaticWindowCreator` entirely** (scene-placed `Window` host, configured in `Start`/`Awake` and self-instantiated under `WindowManager.Shared`): `MapWindow`, `EngineRosterPanel`, `SwitchListPanel`, `Console`. They still benefit from `WindowPersistence.SetInitialPositionSize` and from sitting under `WindowManager` for `HitTest`/`OrderFront`.

---

## `UI.Common.WindowManager`

Singleton MonoBehaviour. `Awake` sets `Shared = this`. The "windows" it manages are simply its **direct children** that have a `Window` component.

```csharp
public class WindowManager : MonoBehaviour
{
    public static WindowManager Shared { get; private set; }            // Awake
    private Window TopmostShownWindow => EnumerateWindows().FirstOrDefault(w => w.IsShown);
    private void OnEnable() => CloseAllWindows();                       // every scene-load wipes shown state
    private void CloseAllWindows();
    public  void CloseTopmostWindow();                                  // routes to topmost.HandleRequestCloseWindow
    public  TWindow GetWindow<TWindow>();                               // throws ArgumentException if missing
    private IEnumerable<Window> EnumerateWindows();                     // REVERSE sibling order (top-of-stack first)
    public  Window HitTest(Vector3 mousePosition);
    public  void Present(Alert alert);                                  // routes to Toast or Console
}
```

### `GetWindow<T>` semantics

```csharp
public TWindow GetWindow<TWindow>()
{
    foreach (Transform item in base.transform)
    {
        TWindow component = item.GetComponent<TWindow>();
        if (component != null)
            return component;
    }
    throw new ArgumentException("Couldn't find TWindow");
}
```

- Walks **direct children only** (not recursive).
- Returns the **first** child with a `TWindow` component. Multiple instances would silently shadow.
- **Throws `ArgumentException`** on miss — never returns null. Callers that want a try-pattern must wrap in `try/catch` or check via `transform`-walk first.
- The `TWindow` type-parameter has **no constraint** — you can ask for any component type, including non-`IBuilderWindow` (e.g., `MapWindow`). Vanilla uses this for `MapWindow.Show()` which lives at `WindowManager.Shared.GetWindow<MapWindow>()`.
- **Init-order trap**: this throws if called before `ProgrammaticWindowCreator.Start` has run. Mods touching `GetWindow<T>` from `Awake` of a `[StateRequiredOnLoad]`-style component will see `ArgumentException`. Defer to a coroutine, `Update` first-frame, or `Messenger<PropertiesDidRestore>`.

### `HitTest` and stacking

`EnumerateWindows()` yields children **in reverse sibling order** so the front-most window (last sibling) is yielded first. `HitTest` returns the front-most `IsShown` window whose RectTransform contains the mouse point. This is what `GameInput.IsMouseOverUI` (and friends) consult to decide whether a click should be routed to game world or to UI.

`Window.OrderFront` is `SetAsLastSibling`; called on `OnPointerDown` and on `ShowWindow`. Standard "click brings to front" UX.

### `Present(Alert)`

```csharp
public void Present(Alert alert)
{
    switch (alert.Style)
    {
    case AlertStyle.Toast:   Toast.Present(alert.Message,
                                  alert.Level != AlertLevel.Error ? Bottom : Middle);
    case AlertStyle.Console: UI.Console.Console.shared.AddLine(alert.Message,
                                  new GameDateTime(alert.Timestamp));
    }
}
```

Routes the cross-network `Alert` payload (`Network.Messages.Alert`) to either toast or console. `WindowManager.Shared.Present(alert)` is the integration point used by `Multiplayer.Broadcast` for AI/system messages — see [`hyperlink-entityref.md`](hyperlink-entityref.md) for the Alert pipeline.

### Patch candidates

| Method | Why patch |
|---|---|
| `WindowManager.GetWindow<T>` | Replace exception-based miss with a try-pattern. Or postfix to register a fallback synthetic window. |
| `WindowManager.OnEnable` (`CloseAllWindows`) | Override the scene-reload-wipe behavior if you persist visibility per save (vanilla persists via PlayerPrefs but always opens hidden). |
| `WindowManager.Present(Alert)` | Custom alert routing (e.g., third style for sticky error banners). |
| `WindowManager.HitTest` | Inject mod windows that are NOT children — but don't bother; reparenting your window under `WindowManager` is the documented path. |

### Gotchas

- **`OnEnable` runs every scene reload.** `CloseAllWindows()` fires before any `Show()` calls. State that needs to survive a load → unload → load cycle (e.g., user's preferred open windows) must be re-applied after Start.
- **`GetWindow<T>` is O(child count)** every call. Cache the result if you call it in a hot path.
- **No `Register/Unregister` API.** Windows are added by reparenting under `transform`; removed by destroying the GameObject. There's no event for "window registered" — patch `ProgrammaticWindowCreator.CreateWindow` for that signal.

---

## `UI.Common.Window` — the host

`Window` is a `[RequireComponent(typeof(RectTransform))] MonoBehaviour, IPointerDownHandler` carrying a `titleLabel`, a `contentRectTransform`, an optional `PanelResizer`, and a `DraggablePanel`. **It owns no content** — it is purely chrome. Content is built via `UIPanel.Create(window.contentRectTransform, …)`.

### Show/hide coroutine and the close-frame-defer

```csharp
private IEnumerator ShowCoroutine(bool shown)
{
    if (!shown) yield return new WaitForEndOfFrame();      // 1-frame defer on close ONLY
    OnShownWillChange?.Invoke(shown);
    UpdateForShown();                                       // SetActive(shown) on every direct child
    OnShownDidChange?.Invoke(shown);
}
```

- **Open is immediate**. `OnShownWillChange(true)` and `OnShownDidChange(true)` fire in the same frame as `ShowWindow()`.
- **Close defers one frame** (waits for end-of-frame). KVO writes that happen during the deferred frame still trigger your subscribed-on-show observers. Dispose subscriptions in `OnShownWillChange(false)` if you want to ignore them.
- The coroutine's `if (!shown)` check means rapidly toggling shown state can re-enter — `SetShown(bool)` guards via `if (IsShown != shown)`, so the rapid-toggle case is debounced at the `IsShown` check.

### `UpdateForShown` zaps direct children's active flag

```csharp
private void UpdateForShown()
{
    foreach (RectTransform item in rectTransform)
    {
        if (item.gameObject == resizer.gameObject)
            item.gameObject.SetActive(_resizable && isShown);
        else
            item.gameObject.SetActive(isShown);
    }
    if (isShown) ClampToParentBounds();
}
```

**This is the framework's biggest extension hazard.** Anything you parent under the `Window` GameObject directly (not under `contentRectTransform`) gets force-toggled with the window. The carve-out is *only* for the `resizer.gameObject`. Practical consequences:

- Mod helpers parented to `window.transform` (e.g., a "minimize" button you pin to the title bar) are deactivated when the window closes — good if intentional, bad if they should keep ticking.
- Sibling overlays parented to `Window` will also vanish on close.
- **Workaround**: parent under `contentRectTransform` (which is itself a child of Window, so it gets toggled — but this is the desired behavior), or under a totally separate root such as `WindowManager.Shared.GetComponent<Canvas>().transform`.

### Surface (full)

```csharp
public TMP_Text titleLabel;
public RectTransform contentRectTransform;
[SerializeField] private PanelResizer resizer;
[SerializeField] private DraggablePanel draggablePanel;
public Action DelegateRequestClose;                                       // override Esc-close
public bool   IsShown { get; private set; }
public string Title { get; set; }                                         // mirror of titleLabel.text
public Vector2 InitialContentSize { get; private set; }
public bool   HasUserResized { get; }                                     // resized OR position-restored
public event Action<bool>    OnShownWillChange, OnShownDidChange;
public event Action<Vector2> OnDidResize, OnDidPosition;
public void   ShowWindow();
public void   CloseWindow();
public void   HandleRequestCloseWindow();                                 // calls DelegateRequestClose ?? CloseWindow
public void   OrderFront();
public void   SetResizable(Vector2 minSize, Vector2 maxSize);
public void   SetPosition(Position p);                                    // LowerLeft/...../CenterRight enum
public void   SetPositionRestoring(Vector2 p);                            // bypasses position-defaults
public void   SetContentSize(Vector2 size);                               // clamps to Screen / GraphicsCanvasScale
public void   SetContentWidth(int w);
public void   SetContentHeight(int h);
public Vector2 GetContentSize();
public Vector2 GetPosition();
public void   UpdateContentSizeFixedHorizontal();                         // for VScrollView panels
public void   FireDidResize(Vector2 sizeDelta);
public void   OnPointerDown(PointerEventData);                            // → OrderFront
```

### `Position` and `Sizing`

```csharp
public enum Position { LowerLeft, LowerRight, UpperLeft, UpperRight, Center, CenterRight }

public readonly struct Sizing : IEquatable<Sizing>
{
    public readonly Vector2Int MinSize, MaxSize;
    public bool IsResizable => MinSize != MaxSize;
    public static Sizing Fixed(Vector2Int size);
    public static Sizing Resizable(Vector2Int minSize, Vector2Int maxSize);
    public static Sizing Resizable(Vector2Int minSize);                   // maxSize = (int.MaxValue, int.MaxValue)
    public Vector2Int Clamp(Vector2Int size);
}
```

`SetPosition(Position p)` snaps to one of the six anchor points (off-screen-ish coordinates — `(-100, -100)` for LowerLeft etc., then `ClampToParentBounds`). `SetPositionRestoring(Vector2)` writes `anchoredPosition` directly and sets `_hasRestoredSize = true` so `HasUserResized` reflects intent.

### Modal vs non-modal

**There is no "modal window" concept in `Window`.** Every `Window` is non-modal — it does not block input. The "modal" experience comes from `ModalAlertController` which uses a *separate canvas* (`canvas` SerializeField on `ModalAlertController`) sorted above `WindowManager`'s canvas, with its own `ModalAlert` prefab. See [`ui-vanilla.md` › ModalAlertController](ui-vanilla.md#uicommontoast--uicommonmodalalertcontroller--the-popup-primitives).

That distinction matters for mods: if you want a "must dismiss before continuing" experience, use `ModalAlertController.Present(Action<UIPanelBuilder, Action> closure, int width)` — that gives you a full builder closure to compose the modal contents AND a `dismiss` callback to invoke when done. Don't try to build a true modal `Window`; nothing prevents users from clicking other windows.

### Patch candidates

| Method | Why patch |
|---|---|
| `Window.ShowWindow` | Postfix to inject sibling-panel show. `OrderFront` already ran. |
| `Window.UpdateForShown` | Add carve-outs for additional non-toggled children. **High-risk** — runs on every show/hide. |
| `Window.SetContentSize` | Veto user-resizes (e.g., enforce content-driven sizing). |
| `Window.HandleRequestCloseWindow` | Intercept Esc-close. Or just set `DelegateRequestClose` from your mod component. |

---

## `UI.IBuilderWindow` / `UI.IProgrammaticWindow` — the protocol

```csharp
public interface IBuilderWindow {
    UIBuilderAssets BuilderAssets { get; set; }                           // injected by ProgrammaticWindowCreator
}

public interface IProgrammaticWindow : IBuilderWindow {
    string         WindowIdentifier { get; }
    Vector2Int     DefaultSize { get; }
    Window.Position DefaultPosition { get; }
    Window.Sizing  Sizing { get; }
}
```

`IBuilderWindow` is just the dependency-injection seam for `BuilderAssets`. There is no required `Awake`, no required `Show`, no required `Populate` method — the per-window class decides its own surface. Vanilla converged on:

- Field `Window _window` (set in `Awake` via `GetComponent<Window>()`).
- Field `UIPanel _panel`.
- Method `Populate()` that calls `_panel = UIPanel.Create(_window.contentRectTransform, BuilderAssets, BuildClosure)`.
- Static `Show()` that returns `WindowManager.Shared.GetWindow<TWindow>()` and calls `.Populate()` then `_window.ShowWindow()`.
- `OnDisable` that disposes `_panel` and nulls it.

This is convention, not contract. You can deviate. Mods that copy the pattern get free integration with `WindowManager.GetWindow<T>` and `WindowPersistence`.

### Patch candidates

| Method | Why patch |
|---|---|
| `ProgrammaticWindowCreator.Start` | Postfix to register additional `IBuilderWindow`-implementing components on the same Canvas. |
| `ProgrammaticWindowCreator.CreateWindow<T>` (both overloads) | Inject mod-specific configuration after the `BuilderAssets` assignment. |

### Gotchas

- **`BuilderAssets` is set AFTER `Awake`** (the `AddComponent<TWindow>` triggers `Awake`, then the next line assigns `BuilderAssets`). Don't read `BuilderAssets` from `Awake` — defer to `Start` or first `Populate`.
- **`window.CloseWindow()` runs immediately after `AddComponent`**, so every newly-registered window is hidden by default. Your `Populate` only runs on first `Show()` — if you need pre-show observers, set them up in `Awake` or in a `OnShownDidChange` subscription.
- **The "self-described" overload calls `SetInitialPositionSize` AFTER `BuilderAssets` is assigned** but uses `val.WindowIdentifier`/`val.DefaultSize`/etc. So those properties must return their final values during `Awake`. Don't compute them lazily from state that isn't ready.

---

## `UI.Common.WindowPersistence` — PlayerPrefs round-trip

Pure-static extension class. The `WindowRecord` struct is JSON-serialized into a single PlayerPrefs key per window.

```csharp
private struct WindowRecord {
    public bool Shown;
    [JsonConverter(typeof(Vector2Converter))] public Vector2 Position;     // normalized [0..1]
    [JsonConverter(typeof(Vector2Converter))] public Vector2 Size;
}

public static void SetInitialPositionSize(this Window window, string identifier,
        Vector2 defaultSize, Window.Position defaultPosition, Window.Sizing sizing)
{
    if (sizing.IsResizable) window.SetResizable(sizing.MinSize, sizing.MaxSize);
    if (TryGetWindowPositionSize(identifier, out _, out var position, out var size)) {
        float scale = Preferences.GraphicsCanvasScale;
        window.SetContentSize(sizing.Clamp(size));
        window.SetPositionRestoring(new Vector2(position.x * Screen.width * scale,
                                                position.y * Screen.height * scale));
    } else {
        window.SetContentSize(defaultSize);
        window.SetPosition(defaultPosition);
    }
    window.OnShownDidChange += _ => DoSaveWindow();
    window.OnDidPosition    += _ => DoSaveWindow();
    window.OnDidResize      += _ => DoSaveWindow();
}
```

### Storage key & format

- PlayerPrefs key: `"window." + identifier`.
- Value: JSON `{"Shown":bool, "Position":{x,y}, "Size":{x,y}}`.
- `Shown` field is **stored but never restored** (the `SetInitialPositionSize` always opens hidden, then `WindowManager.OnEnable` re-closes everything anyway). Vestigial.
- `Position` is stored as fraction-of-(screen × `GraphicsCanvasScale`) so it survives resolution changes within reason.

### Save fan-out

`OnShownDidChange`, `OnDidPosition`, `OnDidResize` each subscribe a delegate that writes PlayerPrefs **on every event**. This is noisy:
- Every show/hide → 1 write.
- Every drag movement → 1 write per `OnPanelDragged` event (debounced by Unity's drag system, but still many per drag).
- Every resize-handle frame → 1 write per `FireDidResize` call (which `PanelResizer` invokes per drag-update frame).

`PlayerPrefs.SetString` on Windows hits the registry. This is not free under sustained drag/resize. Mods that ship many resizable windows may want to debounce.

### Patch candidates

| Method | Why patch |
|---|---|
| `WindowPersistence.SetInitialPositionSize` | Coalesce multi-event writes; or restore `Shown` bit; or change the key prefix scheme. |
| `WindowPersistence.SaveWindow` (private static) | Direct write override. Patch via reflection. |

### Gotchas

- **`shown` is loaded into a discard `out var _`** — yes, the method actively pulls it out and throws it away. The `bool Shown` field on `WindowRecord` exists only because PlayerPrefs needs to round-trip it for legacy reasons.
- **Position is stored as ratio of `Screen.width * GraphicsCanvasScale`.** Change canvas scale and stored positions are still applied as if the previous scale was current. Visible drift is rare because `ClampToParentBounds` runs on restore, but the math is "wrong" — it only happens to land in the right ballpark.
- **No migration / version field.** If the `WindowRecord` shape ever changes, old prefs deserialize-fail silently (try/catch returns false → defaults).

---

## `UI.Builder.UIPanel` — the persistent panel state

`UIPanel` is the long-lived state object behind a `UIPanelBuilder`. It owns:
- `_container` — the `RectTransform` it builds into.
- `_buildClosure` — the user's build delegate, replayed on every `Rebuild`.
- `_children` — child `UIPanel` instances created via nested `HStack`/`VStack`/`AddTabbedPanels`/etc.
- `_keyChangeObservers` — `IDisposable`s registered via `AddObserver`, disposed on `Rebuild`.
- `_timer` — optional `Timer` MonoBehaviour for `RebuildOnInterval`.
- `_registeredForEvents` — flag for Messenger registration; cleared in `UnregisterForEvents`.

### Lifecycle

```csharp
public static UIPanel Create(RectTransform container, UIBuilderAssets assets, Action<UIPanelBuilder> closure)
{
    var p = new UIPanel(null, container, assets, closure);
    p.Rebuild();
    return p;
}

private UIPanel(UIPanel parent, RectTransform container, UIBuilderAssets assets, Action<UIPanelBuilder> closure)
{
    _parent = parent;
    _assets = assets;
    _container = container;
    _container.DestroyChildren();                          // wipe whatever was there
    _buildClosure = closure;
    _id = _nextId++;
    if (_container.GetComponent<LayoutGroup>() == null)
        _container.gameObject.AddComponent<VerticalLayoutGroup>();
}

internal void Rebuild()
{
    DisposeChildren();                                     // dispose child UIPanels, unregister, dispose observers
    if (_container == null) { Log.Warning(...); return; }
    _container.DestroyChildren();
    _buildClosure(new UIPanelBuilder(_container, _assets, this));
    InvokeOnRebuild();
}

private void DisposeChildren()
{
    UnregisterForEvents();
    foreach (UIPanel child in _children) child.Dispose();
    _children.Clear();
}

public void UnregisterForEvents()
{
    if (_registeredForEvents) {
        Messenger.Default.Unregister(this);                // STRIPS ALL HANDLERS for this UIPanel
        _registeredForEvents = false;
    }
    foreach (var obs in _keyChangeObservers) obs.Dispose();
    _keyChangeObservers.Clear();
}
```

### Trigger surface

```csharp
public void RebuildOnEvent<T>();                           // Messenger registration
public void RebuildOnInterval(float seconds);              // Timer MonoBehaviour
public void AddObserver(IDisposable disposable);           // KVO registration; auto-disposed on next Rebuild
public event Action OnRebuild;                             // bubbles to parent via InvokeOnRebuild
internal UIPanel AddChild(RectTransform rt, Action<UIPanelBuilder> closure);
```

### The "Rebuild razes everything" pattern

Every `Rebuild` (whether self-triggered, event-triggered, timer-triggered, or via nested-builder) **destroys every child GameObject under `_container`** (`_container.DestroyChildren()`) and re-runs the closure. Vanilla amortizes this cost via:
- **Per-tick value updates use polling MonoBehaviours** (`TextUpdater`/`SliderUpdater`/`ToggleUpdater`) instead of triggering `Rebuild`.
- **`Rebuild` is reserved for structural changes** (waybill changed, tab switched, train crew added).

For a mod inspector watching N per-tick KVO keys via `RebuildOnEvent<KvoChanged>`-equivalents: this is a stuttering trap. Use `AddLabel(Func<string>, Frequency)` for live values, and `Rebuild` only on coarse events.

### Patch candidates

| Method | Why patch |
|---|---|
| `UIPanel.Rebuild` | Postfix to instrument every rebuild — useful for measuring rebuild frequency in your inspector under load. |
| `UIPanel.DisposeChildren` | Add cleanup for mod-attached state on the container GameObject. |
| `UIPanel.AddChild` | Trace child-panel construction. |
| `UIPanel.UnregisterForEvents` | Inject mod-side cleanup (e.g., dispose your own subscriptions in lockstep). |

### Gotchas

- **`Messenger.Default.Unregister(this)` on `UIPanel` removes ALL handlers for the panel, not per-event.** GalaSoft API behavior. If your patch postfixes `RebuildOnEvent<T>` to add a second handler with the same `this`, vanilla strips both on next `UnregisterForEvents`.
- **`_container.DestroyChildren()` is unconditional.** If you postfix `Rebuild` to add children, they survive until next rebuild. Parent additions to a *sibling* RectTransform if you need persistence.
- **`AddObserver(IDisposable)` doesn't take ownership of the disposable** during construction — it stores it for next-Rebuild disposal. So if `Rebuild` throws mid-closure, observers added BEFORE the throw are still in the set and will be disposed on the *next* Rebuild. Fine in practice; surprising if you're tracing.
- **`_container == null` check in Rebuild** indicates the host GameObject got destroyed mid-life. Logs a warning and returns. Most likely cause: window was destroyed without disposing the `UIPanel`. Vanilla pattern is `OnDisable: _panel?.Dispose(); _panel = null;`.
- **`OnRebuild` event bubbles up the parent chain** — so a parent panel observes the *combined* fan-out of every nested rebuild.

---

## `UI.Builder.UIPanelBuilder` — the fluent DSL

Per-Rebuild **`struct`** (yes, struct — not a class). One is constructed inside `Rebuild` and passed to the build closure; you should never store it. Fields:

```csharp
public struct UIPanelBuilder
{
    public enum Frequency { Fast, Periodic }                              // 0.1s, 1.0s
    public struct ListItem<TValue> : IComparable<ListItem<TValue>> { ... }
    private readonly UIBuilderAssets _assets;
    private readonly RectTransform _container;
    private readonly UIPanel _panel;                                       // class — closures still reach the right tree
    private static GameObject _rebindOverlay;                              // GLOBAL static; lazy-instantiated
    public float? FieldLabelWidth { get; set; }                           // applies to subsequent AddField calls
    public float Spacing { get; set; }                                     // assigns to _container's HorizontalOrVerticalLayoutGroup
}
```

The `_rebindOverlay` static caching is a foot-gun — see `AddInputBindingControl` below.

### Surface — full inventory (43 `Add*` methods)

#### Title / section / row primitives

```csharp
void AddTitle(string title, string subtitle);
void AddSection(string title);                                            // header bar only
void AddSection(string title, Action<UIPanelBuilder> closure, float spacing = 0f);
IConfigurableElement AddField(string label, RectTransform control);
IConfigurableElement AddField(string label, Func<string> valueClosure, Frequency);
IConfigurableElement AddField(string label, string value);
IConfigurableElement AddFieldToggle(string label, Func<bool> get, Action<bool> set, bool interactable=true);
```

`_AddField()` (private) instantiates `_assets.fieldRow`, finds children named `"Label"` and `"Value"` by name. `FieldLabelWidth` (if set on the builder) applies to the label width via `RectTransform.Width(...)`.

#### Buttons

```csharp
IConfigurableElement AddButton(string text, Action a);                    // Default style
IConfigurableElement AddButtonMedium(string text, Action a);
IConfigurableElement AddButtonCompact(string text, Action a);
IConfigurableElement AddButtonCompact(Func<string> textClosure, Action a); // adds TextUpdater @ 0.1s
IConfigurableElement AddButtonSelectable(string text, bool selected, Action a); // Selected vs Default style
```

All instantiate via `_assets.CreateButton(ButtonStyle, parent, action)` which `Object.Instantiate`s the matching prefab and wires `onClick`.

#### Labels & inputs

```csharp
RectTransform AddLabel(string text);
RectTransform AddLabel(string text, Action<TMP_Text> configure);
RectTransform AddLabel(Func<string> closure, Frequency);                  // adds TextUpdater
RectTransform AddLabelMarkup(string markup);                              // calls SetTextMarkup
RectTransform AddLabelEmptyState(string text);                            // grey "empty state" font
RectTransform AddTextArea(string text, Action<string> onLink);
RectTransform AddMultilineTextEditor(string text, string placeholder, Action<string> onChange, Action<string> onEnd);
RectTransform AddInputField(string value, Action<string> onApply, string placeholder=null, int? characterLimit=null);
RectTransform AddInputFieldValidated(string value, Action<string> onApply, string regex, string placeholder=null, int? characterLimit=null);
RectTransform AddInputFieldReportingMark(string value, Action<string> onApply); // = AddInputFieldValidated(...,"[\\p{L}&]", "Up to 6 letters", 6)
RectTransform AddToggle(Func<bool> get, Action<bool> set, bool interactable=true);
RectTransform AddSlider(Func<float> get, Func<string> textClosure, Action<float> set,
                        float min=0, float max=1, bool whole=false, Action<float> editingEnded=null);
RectTransform AddSliderQuantized(Func<float> get, Func<string> textClosure, Action<float> set,
                                 float increment, float min=0, float max=1, Action<float> editingEnded=null);
RectTransform AddDropdown(List<string> values, int sel, Action<int>);
RectTransform AddColorDropdown(List<string> values, int sel, Action<int>);
RectTransform AddColorDropdown(string hexColor, Action<string>);          // hex picker
RectTransform AddOptionsDropdown(IReadOnlyList<DropdownMenu.RowData>, Action<int>);
```

**`AddLabel(Func<string>, Frequency)` is the canonical sub-Rebuild update pattern.** It instantiates a `_assets.labelControl` TMP, AddComponents `TextUpdater`, and configures it to poll `valueClosure()` every 0.1s (Fast) or 1s (Periodic). The TextUpdater coroutine runs while the GameObject is enabled.

`AddInputFieldValidated` reaches into TMPro's private `m_RegexValue` field via reflection — that's a known TMP API gap.

#### Toggle/Slider polling MonoBehaviours

- `TextUpdater` — polls `Func<string>`, sets `TMP_Text.text` on `WaitForSecondsRealtime(_interval)` loop.
- `SliderUpdater` — polls `Func<float>`, sets `Slider.value` (skipping recursion).
- `ToggleUpdater` — polls `Func<bool>`, sets `Toggle.isOn`.
- `Timer` — generic `Action` + `interval`; used by `RebuildOnInterval`.

All four start their coroutine in `OnEnable` and stop in `OnDisable`.

#### Layout primitives

```csharp
RectTransform HStack(Action<UIPanelBuilder>, float spacing = 4);          // creates child UIPanel
VerticalLayoutGroup VStack(Action<UIPanelBuilder>);                        // creates child UIPanel
RectTransform AlertButtons(Action<UIPanelBuilder>);                        // platform-aware Cancel ordering
RectTransform ButtonStrip(Action<UIPanelBuilder>, int spacing=8);
RectTransform VScrollView(Action<UIPanelBuilder>, RectOffset padding=null);
RectTransform HScrollView(Action<UIPanelBuilder>, RectOffset padding=null);
RectTransform HVScrollView(Action<UIPanelBuilder>, RectOffset padding=null);
RectTransform Spacer();                                                    // flexible
void          Spacer(float size);                                          // fixed
void          AddExpandingVerticalSpacer();
RectTransform AddVRule();                                                  // 1px vertical line, color #4E493E
RectTransform AddHRule();
```

`HStack`/`VStack`/`AlertButtons`/etc. all internally call `_panel.AddChild(rectTransform, closure)` to register a child `UIPanel` in the parent's `_children` set. So the child gets independent `Rebuild`/observer/timer state but is disposed when the parent rebuilds.

`AlertButtons` does platform-aware Cancel-button reordering:
- Windows (`WindowsPlayer`/`WindowsEditor`) → Cancel **last**.
- Everything else (macOS, Linux) → Cancel **first** (Apple convention).

#### Composite controls

```csharp
void AddTabbedPanels(UIState<string> selectedTab, Action<UITabbedPanelBuilder>);
void AddListDetail<TValue>(IEnumerable<ListItem<TValue>>, UIState<string> selected,
                            Action<UIPanelBuilder, TValue> closure, float? listWidth=null);
void AddTable(List<TableRow>, List<float> colWidths, TableBuilderConfig config);
void AddBuilderPhoto(string carIdentifier);
void AddInputBindingControl(InputAction inputAction, bool conflict, Action onRebind);
void LazyScrollList(List<object> data, string cellPrefabName);
RectTransform AddLocationPicker(string prompt, ..., Action<IndustryComponent>);
RectTransform AddLocationField(string name, IIndustryTrackDisplayable, Action jump);
```

`AddTabbedPanels` instantiates `_assets.tabView`, hands the `UITabbedPanelBuilder` (a `readonly struct` wrapper around `UI.TabView.TabView`) to your closure, then calls `tabView.FinishedAddingTabs()`. Each `AddTab(title, tabId, closure)` registers a tab whose contents lazily build when the tab is shown.

`AddInputBindingControl` is the entry into the input-rebinding flow used by `BindingsWindow`. See [`input-keybinds.md`](input-keybinds.md). The internal `_rebindOverlay` is a **`static GameObject`** — first creation parents it under `WindowManager.Shared.GetComponent<Canvas>().GetComponent<RectTransform>()` (the Canvas root). **For mods rooted in a different Canvas, the overlay shows up at the wrong z-order** — this is a known foot-gun.

#### Hooks (forward to `UIPanel`)

```csharp
void RebuildOnEvent<T>();                                                  // → _panel.RebuildOnEvent<T>
void RebuildOnInterval(float seconds);                                     // → _panel.RebuildOnInterval
void Rebuild();                                                            // → _panel.Rebuild
void AddObserver(IDisposable disposable);                                  // → _panel.AddObserver
```

### `IConfigurableElement` — fluent chain

```csharp
public interface IConfigurableElement {
    RectTransform RectTransform { get; }
    IConfigurableElement Tooltip(string title, string message);
    IConfigurableElement Tooltip(Func<TooltipInfo> dynamicTooltipInfo);
    IConfigurableElement Disable(bool disable);                            // Selectable.interactable
    IConfigurableElement ChildWidth(int childIndex, float width);
    IConfigurableElement Width(float width);
    IConfigurableElement Height(float height);
}
```

`ConfigurableElement` (`UI.Builder/ConfigurableElement.cs`) is the concrete impl returned by every `AddField`/`AddButton*` method. Use as: `builder.AddField("Label", "Value").Tooltip("title", "msg").Width(200f);`.

`RectTransform`-returning methods (the non-IConfigurableElement ones — `AddLabel`, `AddInputField`, etc.) get fluent chaining via extension methods in `RectTransformLayoutExtensions` (`Width`, `Height`, `FlexibleWidth`, `FlexibleHeight`, `ChildAlignment`, `Tooltip`, `SetTextMarginsTop`).

### Patch candidates

| Method | Why patch |
|---|---|
| `UIPanelBuilder.AddTabbedPanels` | Wrap the closure to inject a tab into any tabbed window (CarInspector, Preferences, Company). |
| `UIPanelBuilder.AddField` (3 overloads) | Universal trace point — every field-row goes through one. Also a convenient place to inject conditional warnings. |
| `UIPanelBuilder.AddInputBindingControl` | Override rebind UX (e.g., key-conflict tooltip, save-on-conflict policy). |
| `UIPanelBuilder.AddSlider` / `AddSliderQuantized` | Custom slider behavior (e.g., logarithmic scale). |
| `UIPanelBuilder._AddField` (private) | Restyle the field row prefab clone — but `Find("Label")`/`Find("Value")` is fragile. |

### Gotchas

- **`UIPanelBuilder` is a `struct`**. Storing it in a field captures by value. The `_panel` field is a class, so closures still mutate the right tree, but `builder.Spacing = 5f` on a struct copy does nothing. Practical impact: if you take a builder ref into a helper method, pass `ref UIPanelBuilder builder` if you need property mutations to persist.
- **`AddInputBindingControl._rebindOverlay` is a process-global static.** First `AddInputBindingControl` call after game launch parents the overlay under `WindowManager.Shared`'s Canvas; subsequent calls reuse the same overlay regardless of which window invoked. Mod windows on a different Canvas see the overlay z-order misalign.
- **`AddTabbedPanels(UIState<string> selectedTab, ...)`** depends on the same `UIState<string>` instance being passed across rebuilds. Re-creating the panel without re-using the same `UIState` resets the active tab to null. Practice: store `UIState` in a `readonly` field on the window class.
- **`UIState<T>`** is just `public class { T Value; }`. No events, no observers, no notification. It's a mutable cell, and `Value` reads see the latest write — but nothing rebuilds when you set it. If you mutate `_uiScaleValue.Value` from outside the rebuild closure, the UI doesn't update until the next user-triggered rebuild.
- **`AddTextLinkReceiverIfNeeded` only fires when text contains `"<link"`** (substring match). If you build a label with hyperlinks injected via TMP markup that includes `<link=..>` (no closing quote on the open tag, e.g., a malformed tag), the receiver is still added because it's a substring match. Conversely, if your text has links added later via `SetText`, the receiver isn't there.
- **`AddLabel(Func<string>, Frequency)` doesn't poll while the GameObject is disabled** (`TextUpdater.OnDisable` stops the coroutine). Tabbed panels: hidden tab content is disabled, so its TextUpdaters pause — re-enabling re-runs the closure once on `OnEnable` for the initial value.
- **`Spacing` setter requires the container to have a `HorizontalOrVerticalLayoutGroup`.** `UIPanel`'s constructor adds a `VerticalLayoutGroup` if no `LayoutGroup` is present, so this works for direct panel containers. But if you build a custom container (e.g., a raw RectTransform with no layout group), `Spacing` get/set throws `NullReferenceException`.
- **`HStack`/`VStack`/etc. CreateRectView with `HideFlags.DontSave`** — these are scene-only, won't persist via save/load (which is correct).
- **`InstantiateInContainer<T>` uses `_container` as the parent.** Inside a nested `HStack` closure, `_container` is the inner stack's RectTransform — so `AddLabel` inside an `HStack` parents into the HStack. Trivially correct, but useful when reasoning about which parent your widget lands under during postfix patches.
- **No `IConfigurableElement` for `Add*` methods that return `RectTransform`.** Want a tooltip on an `AddInputField`? Use `myInputField.Tooltip("title", "msg")` extension method, not the IConfigurableElement chain.

---

## `UIBuilderAssets` — the shared prefab catalog

ScriptableObject (`[CreateAssetMenu(menuName = "Railroader/UI/UIBuilderAssets")]`) holding every prefab the builder DSL instantiates. There is **one** instance — assigned to `ProgrammaticWindowCreator.builderAssets` in the scene, then forwarded to every window via `IBuilderWindow.BuilderAssets`.

```csharp
public PanelTitle panelTitle;
public RectTransform sectionHeader;
public RectTransform fieldRow;
public RectTransform locationField;

[Header("Buttons")]
public Button button, buttonSelected, buttonCompact, buttonMedium;

[Header("Controls")]
public TMP_Text labelControl, labelEmpty;
public RectTransform textArea, multilineTextEditor;
public TMP_InputField inputField;
public TMP_Dropdown dropdownControl;
public DropdownMenu dropdownOptionsControl;
public RectTransform colorDropdownControl;
public DropdownColorPicker dropdownColorPicker;
public DropdownLocationPicker dropdownLocationPicker;
public Toggle toggleControl;
public CarControlSlider carControlSlider;

[Header("Rebinding")]
public RebindActionUI rebindControl;
public GameObject rebindOverlay;

[Header("Containers")]
public TabView tabView;
public ListDetailController listDetailController;
public ScrollRect scrollRectVertical, scrollRectHorizontal, scrollRectHorizontalVertical;
public TableBuilder tableBuilder;
public Sprite uiSprite;

[Header("List Cell Prefabs")]
public ListCellPrefab[] listCellPrefabs;                                   // (string name, GameObject prefab)

[Header("Specialized")]
public BuilderPhoto builderPhoto;

public enum ButtonStyle { Default, Selected, Compact, Medium }
public (RectTransform, TMP_Text) CreateButton(ButtonStyle style, Transform parent, Action action);
```

### Mutation hazard

This is a singleton **ScriptableObject** held by reference. Assigning to a field on the catalog mutates global state for every future `Add*` call. Don't do `_assets.button = myButton;` in a mod — instantiate copies if you need a different button prefab. Or build your own widget API atop your own prefab references.

### Practical mod usage

- **Re-skin existing widgets**: replace SerializedField references on the ScriptableObject asset (requires editing the asset, not runtime patch). Not practical mid-mod.
- **Reuse vanilla style**: grab `WindowManager.Shared`'s component chain to find the live `UIBuilderAssets`; instantiate prefabs directly via `Object.Instantiate(assets.fieldRow, parent)`. But the simplest mod path is to build your panels via `UIPanel.Create(myContainer, vanillaAssets, closure)` — your panel uses vanilla styling automatically.
- **Add new widget types**: register your own ScriptableObject of a different name; instantiate from it manually. There's no way to extend the `UIPanelBuilder` `Add*` surface short of patching.

---

## `UI.Builder.UIState<T>` — the trivial state cell

```csharp
public class UIState<T>
{
    public T Value;
    public UIState(T value) { Value = value; }
}
```

That's the entire class. Used as:
- The selected-tab tracker for `AddTabbedPanels`.
- The selected-row tracker for `AddListDetail`.
- The intermediate slider value in Preferences (so the editing-ended commit can read the latest).

**No reactivity.** Nothing fires when `Value` changes. If you want notification, wrap mutations in your own setter helper that calls `_panel.Rebuild()`.

The instance must be the *same reference* across rebuilds for tab/listdetail state to persist — vanilla pattern is `private readonly UIState<string> _selectedTabState = new(null);` as a class field.

---

## `Frequency` enum (for poll-based updaters)

```csharp
public enum Frequency { Fast, Periodic }                                   // = 0.1s, 1.0s
```

Mapped inside `AddLabel(Func<string>, Frequency)`:

```csharp
float interval = updateFrequency switch {
    Frequency.Fast     => 0.1f,
    Frequency.Periodic => 1f,
    _                  => 1f,
};
```

Used by `AddLabel(Func<string>, Frequency)`, `AddField(string, Func<string>, Frequency)`. NOT used by `AddSlider`/`AddToggle`/`AddButtonCompact(Func<string>, …)` — those are hard-coded to 0.1s. There's no `Frequency.Slow` (e.g., 5s) — pad your closure with a cache if you want lower-frequency updates.

---

## Spine 2: how a Component descriptor becomes MonoBehaviours

```
Asset pack on disk:
   <persistentDataPath>/AssetPacks/<packId>/
      ├── Bundle               ← AssetBundle of model GameObjects, AnimationMaps, etc.
      ├── Catalog.json         ← AssetPack.Common.AssetPackCatalog
      └── Definitions.json     ← serialized Container of ContainerItems
                                  └── ContainerItem.Definition : ObjectDefinition
                                       └── List<Model.Definition.Component> Components
                                            (polymorphic via [JsonConverter(JsonSubtypes), KnownSubType(...)])

Game start:
   PrefabStore.Create()                                                    ← Model.Database/PrefabStore.cs
      │  AssetPackRuntimeStore per pack
      │  Container() → ContainerSerialization.Deserialize(Definitions.json)
      │     → JsonConvert with JsonSubtypes → typed Component subtypes
      ▼
Per-car spawn:
   Car.Setup → ComponentLifetime.Static pass:
      │  for each component in Definition.EnabledComponentsForLifetime(Static):
      │     ComponentSetup.Setup(name, component, ctx, parent, observe, prefabInst)
      │        new GameObject(component.Name)             ← hidden, parented under car body
      │        go.SetActive(false)                         ← built while inactive
      │        ComponentBuilderContext ctx = new(...)
      │        ComponentFactory.BuildComponent(component, ctx)            ← Model/ComponentFactory.cs:34
      │           PrepareBuildersIfNeeded()                                ← lazy attribute scan
      │              types = typeof(ComponentFactory).Assembly.GetTypes()  ← Assembly-CSharp.dll ONLY
      │              foreach type with [ComponentBuilder]:
      │                 _builders[builder.ComponentType] = builder
      │           if _builders.TryGetValue(component.GetType(), out builder)
      │              builder.Build(ctx, component)
      │           else if _builders.TryGetValue(component.BaseType, out builder)
      │              builder.Build(ctx, component)                         ← ONE-LEVEL FALLBACK
      │           else
      │              Log.Warning("No builder for {type}")                  ← silent drop
      │        go.SetActive(true)                                          ← OnEnable fires now
      ▼
Car.HandleModelsLoaded → ComponentLifetime.Model pass:
      │  same loop, parented under loaded body model
      ▼
Per-builder Build(ctx, component):
   ctx.InstantiatePrefab<T>(name, ctx.GameObject.transform)                ← e.g., HeadlightController
   ctx.Resolve(component.Animation/Material/Transform)                     ← AnimationMap / MaterialMap lookup
   ctx.ObserveProperty(key, observer)                                      ← KVO subscription
```

Two passes per car:
1. `ComponentLifetime.Static` — runs in `Car.Setup`, parented to the Car GameObject. Used for components that don't need the loaded model (e.g., `LoadTargetComponent`).
2. `ComponentLifetime.Model` — runs in `Car.DidLoadModels`, parented under the body model. Most components.

`Definition.EnabledComponentsForLifetime(lifetime)` filters by `Component.Enabled` AND by `[Component(..., Lifetime=…)]` attribute on the C# class. **The attribute on the C# class governs lifetime, not a JSON property.**

---

## `Model.IComponentBuilder` and `[ComponentBuilder]`

```csharp
// Model/IComponentBuilder.cs
public interface IComponentBuilder
{
    Type ComponentType { get; }                                            // the Component subclass this builder handles
    void Build(ComponentBuilderContext ctx, Component component);
}

// Model.ComponentBuilders/ComponentBuilderAttribute.cs
[AttributeUsage(AttributeTargets.Class)]
public class ComponentBuilderAttribute : Attribute { }                     // marker only — no constructor args
```

The attribute carries no metadata. The `ComponentType` getter on each builder is what binds builder→descriptor. By convention, `ComponentType` returns `typeof(FooComponent)` and `Build` does `_Build(ctx, (FooComponent)component)`. The cast is unsafe — if the registry were ever malformed (multiple builders claiming the same `ComponentType`), the last-registered wins (`_builders[builder.ComponentType] = builder` overwrites).

### `Model.ComponentFactory`

```csharp
public static class ComponentFactory
{
    private static Dictionary<Type, IComponentBuilder> _builders;          // lazy

    private static void PrepareBuildersIfNeeded()
    {
        if (_builders != null) return;
        _builders = new Dictionary<Type, IComponentBuilder>();
        Type[] types = typeof(ComponentFactory).Assembly.GetTypes();       // ASSEMBLY-CSHARP ONLY
        foreach (Type type in types)
        {
            if (type.GetCustomAttributes(typeof(ComponentBuilderAttribute), inherit: true).Length != 0)
                Register((IComponentBuilder)Activator.CreateInstance(type));
        }
        static void Register(IComponentBuilder builder)
            => _builders[builder.ComponentType] = builder;
    }

    public static void BuildComponent(Component component, ComponentBuilderContext ctx)
    {
        PrepareBuildersIfNeeded();
        Type type = component.GetType();
        if (_builders.TryGetValue(type, out var value))
            value.Build(ctx, component);
        else if (type.BaseType != null && _builders.TryGetValue(type.BaseType, out value))
            value.Build(ctx, component);
        else
            Log.Warning("No builder for {type}", type);
    }
}
```

### Critical extension constraints

1. **The scan is `typeof(ComponentFactory).Assembly` — i.e., only `Assembly-CSharp.dll`.** Mod-defined `[ComponentBuilder]` classes in mod DLLs are **never discovered**. To register a mod-defined builder, you must either:
   - Patch `PrepareBuildersIfNeeded` to widen the scan (e.g., `AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes())`), or
   - Reflect into `_builders` post-attribute-scan and add your entry directly. The dictionary is private — `typeof(ComponentFactory).GetField("_builders", BindingFlags.NonPublic|BindingFlags.Static).GetValue(null)`.

2. **`PrepareBuildersIfNeeded` is lazy and called from `BuildComponent`.** First car spawn triggers the scan. Patch must be in place before the first `BuildComponent` call. For Harmony, this means before any car loads — register your builder in your mod's `Awake` of an early-bootstrap MonoBehaviour, or in a `[HarmonyPatch(typeof(ComponentFactory), nameof(PrepareBuildersIfNeeded))]` prefix that does `_builders ??= new Dictionary<...>(); _builders[typeof(MyComponent)] = new MyBuilder();` and lets the rest of vanilla scan run after.

3. **One-level base-class fallback only.** A subtype `MySubComponent : VanillaComponent` without its own builder will reuse `VanillaComponentBuilder`. A subtype-of-subtype `MySubSubComponent : MySubComponent` will fall back exactly one level — to `MySubComponent`. If `MySubComponent` has no builder either, the lookup fails and you get `Log.Warning("No builder for {type}")` — **the component is silently dropped**.

4. **`JsonSubtypes` registration is a separate hard gate.** `Component` is `[JsonConverter(typeof(JsonSubtypes), "Kind")]` with hardcoded `[JsonSubtypes.KnownSubType(typeof(FooComponent), "Foo")]` attributes. JSON deserialization throws on unknown `Kind` strings. Adding a new component type requires:
   - **(a) JSON deserialization works**: register your `Kind` value with `JsonSubtypes`. Newtonsoft.Json's `JsonSubtypes` library supports runtime registration via `JsonSubtypesConverterBuilder` — the cleanest route is to patch the `ContainerSerialization.Deserialize` flow to register your builder before the static `JsonSubtypes` attributes get inspected. In practice this means patching `ContainerSerialization` to inject a custom `JsonConverter`. See [`car-definitions.md` › Adding a new component type](car-definitions.md#adding-a-new-component-type).
   - **(b) Builder factory finds your builder**: see point 1.
   - **(c) Editor surfaces it**: see "CarEditorWindow" below.

5. **Builder code runs while the component GameObject is `SetActive(false)`.** `OnEnable` of any added MonoBehaviour fires AFTER `Build` returns when `ComponentSetup.Setup` re-enables the GameObject. Defer post-init to `OnEnable` or a coroutine; the `ctx.GameObject.AddComponent<MyMb>()` returns a *disabled* component.

### Vanilla builder list (28 in `Model.ComponentBuilders/`)

`AggregateLoadModelComponentBuilder`, `BellComponentBuilder`, `ChuffComponentBuilder`, `ClassLightComponentBuilder`, `ColorizerComponentBuilder`, `CompressorComponentBuilder`, `CylinderCockComponentBuilder`, `DecalComponentBuilder`, `DerailedEffectComponentBuilder`, `DetailModelComponentBuilder`, `DieselExhaustComponentBuilder`, `DynamoComponentBuilder`, `FireboxEffectComponentBuilder`, `GaugeComponentBuilder`, `HeadlightComponentBuilder`, `HornComponentBuilder`, `LadderComponentBuilder`, `LegacyMapMaskComponentBuilder`, `LightFixtureComponentBuilder`, `LoadAnimationComponentBuilder`, `LoadModelComponentBuilder`, `LoadTargetComponentBuilder`, `MapMaskComponentBuilder` (handles `RectangleMapMaskComponent` AND `CircleMapMaskComponent`), `MarkerLightComponentBuilder`, `PrefabControlComponentBuilder`, `RadialControlComponentBuilder`, `SeatComponentBuilder`, `ToggleAnimationComponentBuilder`, `ToggleControlComponentBuilder`, `WhistleComponentBuilder`.

Full Kind→Class→Builder table is in [`car-definitions.md` › Known component types](car-definitions.md#known-component-types-componentkind-strings).

---

## Builder-time vs lifecycle-time init split

`IComponentBuilder.Build` runs **once at body-load time** to AddComponent the runtime MonoBehaviours. Anything that happens *after* (per-tick updates, ongoing effects, animations responding to state) belongs in those MonoBehaviours' own `Awake`/`OnEnable`/`Update`/coroutines. Three patterns:

### Pattern A — trivial: just instantiate and configure

```csharp
[ComponentBuilder]
public class HeadlightComponentBuilder : IComponentBuilder
{
    public Type ComponentType => typeof(HeadlightComponent);
    public void Build(ComponentBuilderContext ctx, Component component)
        => _Build(ctx, (HeadlightComponent)component);
    private void _Build(ComponentBuilderContext ctx, HeadlightComponent component)
    {
        var hc = ctx.InstantiatePrefab<HeadlightController>("headlight", ctx.GameObject.transform);
        hc.LightEnabled = component.LightEnabled;
        hc.Direction    = component.Forward ? Forward : Reverse;
    }
}
```

Field copy, done. `HeadlightController` does its own `Awake` for KVO subscriptions.

### Pattern B — depends on resolved animations + animator

```csharp
[ComponentBuilder]
public class BellComponentBuilder : IComponentBuilder
{
    public void Build(ComponentBuilderContext ctx, Component component)
        => _Build(ctx, (BellComponent)component);
    private void _Build(ComponentBuilderContext ctx, BellComponent component)
    {
        string name = (ctx.GameObject.GetComponentInParent<Car>()?.CarType == "LD")
                       ? "bell-diesel" : "bell-steam";
        Bell bell = ctx.InstantiatePrefab<Bell>(name, ctx.GameObject.transform);
        bell.player.mixerGroup = AudioController.Group.LocomotiveBell;
        bell.animationClip = ctx.Resolve(component.Animation);              // AnimationMap lookup
        bell.animator = ctx.AnimatorGameObject.GetComponent<Animator>();    // body-level animator
    }
}
```

Lifecycle-time wiring — the animator and clip exist because the Model lifetime runs after model-load.

### Pattern C — two-phase via DidLoadModels (the chuff pattern)

`ChuffComponentBuilder` itself is trivial — it just instantiates `chuff` and `smokestack` prefabs. The interesting two-phase wiring happens later in `Car.DidLoadModels` for steam locomotives, where `IChuffProvider.Configure` connects the chuff (audio+particle) to the wheel-driven chuff source. See [`locomotive-architecture.md` › ChuffComponentBuilder two-phase init](locomotive-architecture.md) for the full chain.

The takeaway: **builders never know about the broader Car or about peer components.** They build localized MonoBehaviours. Cross-component wiring happens in `Car.DidLoadModels` or via `KeyValueObject` shared-key observation patterns (`ToggleAnimationComponentBuilder` uses `KeyValuePickableToggle` + `KeyValueBoolAnimator` sharing a KVO key).

### `ComponentBuilderContext` — the builder's API surface

```csharp
public readonly struct ComponentBuilderContext : IDefinitionReferenceResolver, IPrefabInstantiator
{
    public GameObject GameObject { get; }                                  // the per-component sub-GO
    public GameObject AnimatorGameObject { get; }                          // body's AnimationMap.gameObject
    public CarColorController CarColorController { get; }
    public string ObjectName { get; }                                      // for diagnostics

    public T InstantiatePrefab<T>(string name, Transform parent) where T : Component;
    public Transform Resolve(TransformReference);                          // walks GameObject.transform.parent
    public AnimationClip Resolve(AnimationReference);                      // via _animationMap
    public bool TryResolve(AnimationReference, out AnimationClip);
    public Material Resolve(MaterialReference);                            // logs error if _materialMap is null
    public void ObserveProperty(string key, Action<Value> observer);       // KVO subscription
    public void ObserveProperty(PropertyChange.Control control, Action<Value> observer);
}
```

`_observeProperty` is closed over by `Car.SetupComponent` and routes to either `Observers` (Static) or `_controlObservers` (Model). The latter is **disposed on `Car.UnloadModels`**, so model-lifetime KVO observers don't leak when models cull-unload (see [`rendering-pipeline.md` › CarCuller LOD bands](rendering-pipeline.md)).

`InstantiatePrefab<T>(name, parent)` reaches into `TrainController.Shared.PrefabInstantiator` — a registry of named prefabs (`"headlight"`, `"chuff"`, `"smokestack"`, `"bell-diesel"`, `"bell-steam"`, etc.) loaded from the `shared` asset bundle. **Mod builders that need new prefabs** must either ship them in their own asset pack and use `prefabStore.LoadAssetAsync` directly, or extend `IPrefabInstantiator`'s registry — see [`asset-packs.md`](asset-packs.md).

---

## `CarEditorWindow.ConfigureAddComponentDropdown` — the editor surface

`UI.CarEditor.CarEditorWindow.cs:265`:

```csharp
private void ConfigureAddComponentDropdown()
{
    addComponentDropdown.ClearOptions();
    _addComponentOptions.Clear();
    List<string> names = new List<string>();
    Type[] types = typeof(ComponentAttribute).Assembly.GetTypes();         // Definition.dll
    foreach (Type type in types)
    {
        var attrs = type.GetCustomAttributes(typeof(ComponentAttribute), inherit: true);
        if (attrs.Length != 0
            && (attrs[0] as ComponentAttribute).IsCompatibleWith(_item.Definition)
            && type.GetCustomAttributes(typeof(HideInEditorAttribute), inherit: true).Length == 0)
        {
            string str = DisplayNameForComponentType(type);
            AddOption(type, str);
        }
    }
    names.Insert(0, "Add Component");
    addComponentDropdown.AddOptions(names);
    void AddOption(Type item, string item2) {
        _addComponentOptions.Add(item);
        names.Add(item2);
    }
}
```

**The editor scans `typeof(ComponentAttribute).Assembly` — i.e., `Definition.dll`** (the assembly that contains the `ComponentAttribute` class). Mod-defined `Component` subclasses in *mod* DLLs are NOT discovered.

Adding `[HideInEditor]` to a `Component` class hides it from this dropdown. Vanilla uses this for `DerailedEffectComponent` (which `Car.SetupComponents` injects programmatically — see [`car-definitions.md`](car-definitions.md)) and a couple of others.

**To add a mod component to the editor dropdown**: patch `ConfigureAddComponentDropdown` to also scan your mod assembly. Combine with patches to `JsonSubtypes` registration on `Component` and `ComponentFactory.PrepareBuildersIfNeeded`'s assembly scan.

`AddComponentDropdownChanged(int index)` constructs the new component via `Activator.CreateInstance(_addComponentOptions[index - 1])` — so your `Component` subclass must have a parameterless constructor.

`DefinitionEditorModeController.NewContainerItem` (`DefinitionEditorModeController.cs:195`) offers a hardcoded list of new-definition buttons (`Steam Locomotive`, `Diesel`, `Car`, `Truck`, `Whistle`, `Scenery`, `Material`). To add a mod-defined `ObjectDefinition` subclass to that list, patch `DrawGUINewItem`.

---

## Mod recipes for the component-builder framework

### Adding a new component type (full recipe)

Cross-references [`car-definitions.md` › Adding a new component type](car-definitions.md#adding-a-new-component-type) — restated here from the framework angle.

**Required patch points (4):**

1. **`JsonSubtypes` registration on `Component`** — without this, JSON deserialization of `Definitions.json` containing your `Kind` string throws. Newtonsoft.Json's `JsonSubtypes` does support runtime registration via `JsonSubtypesConverterBuilder.Of(typeof(Component), "Kind").RegisterSubtype(typeof(MyComponent), "MyKind").SerializeDiscriminatorProperty().Build()`. Patch `ContainerSerialization.Deserialize` to inject this converter (or replace the `[JsonConverter]` attribute on `Component` reflectively).

2. **`ComponentFactory.PrepareBuildersIfNeeded`** — patch to widen the assembly scan, OR reflectively populate `_builders` post-scan. For a clean fix:
   ```csharp
   [HarmonyPatch(typeof(ComponentFactory), "PrepareBuildersIfNeeded")]
   static class WidenBuilderScan {
       static void Postfix() {
           var field = typeof(ComponentFactory).GetField("_builders",
                            BindingFlags.NonPublic | BindingFlags.Static);
           var dict = (Dictionary<Type, IComponentBuilder>)field.GetValue(null);
           dict[typeof(MyComponent)] = new MyComponentBuilder();
       }
   }
   ```
   Plus your `MyComponentBuilder` class with `[ComponentBuilder]` (the attribute is harmless even though vanilla scan never sees it — keeps your code consistent).

3. **`UI.CarEditor.CarEditorWindow.ConfigureAddComponentDropdown`** — patch to widen the type scan to include your assembly. Optional if you don't need editor surfacing.

4. **(Optional) `[Component(ComponentDefinitionMask.Car|Scenery, ComponentLifetime.Static|Model)]`** on your `Component` subclass — this is the attribute that governs which lifetime pass picks up your component and which `ObjectDefinition` types it's compatible with. Without the attribute, your component is treated as `DefinitionMask.Any`, `Lifetime.Model`, and the editor's `IsCompatibleWith` check passes by default (because `Any` → match anything). Set the attribute correctly for the right lifetime pass.

**Silent failure modes:**
- Builder not registered → `Log.Warning("No builder for {type}")` and component dropped at car spawn.
- `JsonSubtypes` not registered → `JsonSerializationException` during pack load → pack removed from `_stores` entirely (`DefinitionChecker.Check` failure cascades).
- `[Component]` attribute missing or wrong lifetime → component listed in `Definition.Components` but skipped in the relevant `EnabledComponentsForLifetime` pass.
- `[HideInEditor]` accidentally present → editor hides the component but runtime still loads it. Usually intentional for runtime-injected components.

### Subclassing an existing component (no new builder)

Define `MyHeadlightComponent : HeadlightComponent`. Vanilla's `HeadlightComponentBuilder` will be reused via the one-level base-class fallback. This works without patching `ComponentFactory`, but you still need the `JsonSubtypes` registration and the editor patch if you want surfacing. Limited utility — fields you add to the subclass are *not* read by `HeadlightComponentBuilder` since it casts to `HeadlightComponent` and reads only the base fields. Useful for "tag" components that other code inspects via type-check.

### Replacing an existing builder

Reflectively overwrite `_builders[typeof(VanillaComponent)] = new MyReplacementBuilder();`. The next car spawn uses your builder. Be cautious — vanilla `Car.cs` may directly query the resulting MonoBehaviour types; if your builder produces different MonoBehaviours, downstream consumers may NRE.

---

## Cross-cutting types

| Type | File | Note |
|---|---|---|
| `Window.Position` enum | `Window.cs:15` | LowerLeft / LowerRight / UpperLeft / UpperRight / Center / CenterRight |
| `Window.Sizing` struct | `Window.cs:25` | `Fixed(Vector2Int)`, `Resizable(min[, max])`; `IsResizable` flag |
| `IConfigurableElement` | `IConfigurableElement.cs:6` | Returned by every `AddField`/`AddButton*`. Fluent: `Tooltip` / `Disable` / `Width` / `Height` / `ChildWidth` / `RectTransform` |
| `ConfigurableElement` | `UI.Builder/ConfigurableElement.cs` | Concrete impl |
| `RectTransformLayoutExtensions` | `UI.Builder/RectTransformLayoutExtensions.cs` | `Width` / `Height` / `FlexibleWidth` / `FlexibleHeight` / `ChildAlignment` / `Tooltip` / `SetTextMarginsTop` / `SetFrameFillParent` |
| `LayoutGroupExtensions` / `LayoutGroupFluentExtensions` | `UI.Builder/LayoutGroup*.cs` | Fluent `.Padding(...)`, `.ChildAlignment(...)` |
| `Frequency` enum | `UIPanelBuilder.cs:21` | `Fast` (0.1s) / `Periodic` (1.0s) |
| `UIPanelBuilder.ListItem<TValue>` | `UIPanelBuilder.cs:27` | Identifier+Value+Section+Text quartet for `AddListDetail` |
| `UITabbedPanelBuilder` (readonly struct) | `UITabbedPanelBuilder.cs:6` | Wraps `UI.TabView.TabView`; `AddTab(title, tabId, closure)` + `Finish()` |
| `TextUpdater` / `SliderUpdater` / `ToggleUpdater` / `Timer` | `UI.Builder/*Updater.cs`, `Timer.cs` | Polling MonoBehaviours auto-added by `Add*` methods |
| `UIBuilderAssets` | `UIBuilderAssets.cs:12` | Single shared prefab catalog; `[CreateAssetMenu]` ScriptableObject |
| `Model.IComponentBuilder` | `Model/IComponentBuilder.cs:6` | `(Type ComponentType, void Build(ctx, component))` |
| `Model.ComponentBuilders.ComponentBuilderAttribute` | `Model.ComponentBuilders/ComponentBuilderAttribute.cs:6` | `[AttributeUsage(AttributeTargets.Class)]`; no params |
| `Model.ComponentBuilderContext` | `Model/ComponentBuilderContext.cs:12` | `readonly struct` — GameObject + AnimatorGameObject + InstantiatePrefab + Resolve(*Reference) + ObserveProperty |
| `Model.ComponentSetup.Context` | `Model/ComponentSetup.cs:12` | AnimationMap + MaterialMap + CarColorController bundle |
| `IPrefabInstantiator` | `AssetPack.Common/...` | `T InstantiatePrefab<T>(string name, Transform parent) where T : Component` |
| `IDefinitionReferenceResolver` | `Model/IDefinitionReferenceResolver.cs` | `Resolve(TransformReference|AnimationReference|MaterialReference)` |
| `Model.Definition.ComponentLifetime` enum | (Definition.dll) | `Static` / `Model` |
| `Model.Definition.ComponentDefinitionMask` enum | (Definition.dll) | `Any` / `Car` / `Scenery` |
| `Model.Definition.ComponentAttribute` | (Definition.dll) | `[Component(DefinitionMask, Lifetime)]` on `Component` subclass |
| `Model.Definition.HideInEditorAttribute` | (Definition.dll) | Hides from `CarEditorWindow.ConfigureAddComponentDropdown` |

---

## High-value findings recap

- **`UpdateForShown` zaps direct children's active flag** — single most common Window-host extension foot-gun. Carve-out only for `resizer.gameObject`.
- **`Window.GetWindow<T>` throws on miss AND on early-call**. No try-pattern, no nullable return. Defer past `ProgrammaticWindowCreator.Start`.
- **`UIPanel.UnregisterForEvents` calls `Messenger.Default.Unregister(this)` which strips ALL handlers** for the panel. GalaSoft API behavior. Postfix-added handlers using the same `this` get nuked.
- **Every `Rebuild` razes the tree** (`_container.DestroyChildren()`). Per-tick rebuild loops stutter. Use `AddLabel(Func<string>, Frequency)` for live values.
- **`UIState<T>` has no reactivity.** It's a public field on a class. No event, no setter, no observer.
- **`AddInputBindingControl._rebindOverlay` is a process-global static** parented under `WindowManager.Shared`'s Canvas. Mods on a different Canvas see z-order misalign.
- **`AlertButtons` reorders the Cancel button platform-aware** (Windows: last; macOS/Linux: first). The detection is by `TMP_Text.text == "Cancel"` substring match — i18n-fragile.
- **`WindowPersistence` writes PlayerPrefs on every show/hide/move/resize event.** Sustained drag/resize hits the registry hot.
- **`ComponentFactory.PrepareBuildersIfNeeded` scans only `Assembly-CSharp.dll`**. Mod-defined builders are silently invisible. Patch the scan or directly populate `_builders`.
- **`ComponentFactory.BuildComponent` has one-level base-class fallback** then `Log.Warning("No builder for {type}")` and silent drop. Subtype-of-subtype falls off the cliff.
- **`CarEditorWindow.ConfigureAddComponentDropdown` scans `typeof(ComponentAttribute).Assembly`** (Definition.dll) — a different assembly from the builder factory's scan. Both must be widened independently for full mod component support.
- **`ComponentSetup.Setup` builds the GameObject `SetActive(false)`** then re-enables. `Build` runs while disabled; `OnEnable` of any added MonoBehaviour fires AFTER `Build` returns. Don't access scene state in `Build` that requires `OnEnable` to have run.
- **`JsonSubtypes` registration on `Component` is via attribute** — no public registry. Adding a `Kind` string requires injecting a custom `JsonConverter` or patching `ContainerSerialization`. Without registration, JSON load throws and the entire pack drops.
- **The `_builders` cast (`(IComponentBuilder)Activator.CreateInstance(type)`) requires a parameterless ctor** on the builder class. Same constraint applies to `Activator.CreateInstance(_addComponentOptions[index - 1])` in the editor for the `Component` subclass.
- **There is no public "Window did register" event.** Patch `ProgrammaticWindowCreator.CreateWindow<T>` (both overloads) for that signal.

---

## Cross-references

- **Concrete vanilla windows + Toast/ModalAlertController** — see [`ui-vanilla.md`](ui-vanilla.md).
- **Component-descriptor schema, asset packs, `CarDefinition`, full Kind→Class→Builder table** — see [`car-definitions.md`](car-definitions.md).
- **`ComponentFactory.PrepareBuildersIfNeeded` Assembly-CSharp scan + `JsonSubtypes` registration constraint** — see [`cars-cargo.md`](cars-cargo.md).
- **`ChuffComponentBuilder` two-phase init pattern (builder + DidLoadModels wiring)** — see [`locomotive-architecture.md`](locomotive-architecture.md).
- **`AddInputBindingControl` and `BindingsWindow` integration** — see [`input-keybinds.md`](input-keybinds.md).
- **`WindowManager.GetWindow<T>` singleton-window routing pattern (TutorialManager case)** — see [`tutorial.md`](tutorial.md).
- **Asset pack discovery, `PrefabStore.Create`, `IPrefabInstantiator`** — see [`asset-packs.md`](asset-packs.md).
- **`Hyperlink` URI emission and `TextLinkReceiver` integration** — see [`hyperlink-entityref.md`](hyperlink-entityref.md).
- **`CanvasScaleChanged` Messenger event (UI scale propagation)** — see [`events-catalog.md`](events-catalog.md) and [`ui-vanilla.md` › CanvasSettingsApplicator](ui-vanilla.md#gamesettingscanvassettingsapplicator--ui-scale-plumbing).
