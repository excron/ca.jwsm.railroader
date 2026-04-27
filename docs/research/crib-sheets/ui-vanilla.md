# UI (Vanilla) — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Wear & Durability](wear-durability.md) · [Couplers](couplers.md) · [Consist Integration](consist-integration.md)

Vanilla Railroader's player-facing UI is uGUI + TextMeshPro built on a small in-house "fluent panel builder" (`UIPanelBuilder`) that constructs prefab-based controls into a `Window` host. There is **no UI Toolkit content in vanilla** — the only `UnityEngine.UIElements` reference is `GameInput.IsMouseOverUI` hit-testing for *modder*-instantiated UIDocuments. Most windows follow one of two patterns: a `MonoBehaviour : IBuilderWindow` paired with a singleton `Window` instantiated by `ProgrammaticWindowCreator`, or a hand-rolled prefab with TMP_Text fields the script pokes via SerializeField. The HUD (`LocomotiveControlsUIAdapter`) is the latter; CarInspector / Preferences / Company / Time / Equipment are the former. Modders who want to integrate either patch a builder method (rare and brittle) or — far more commonly — present their own panel alongside or in place of vanilla's, reading the same model state and listening to the same `Messenger` / KVO events. This sheet maps the surface so you know which choice fits each panel.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `UI.Common.Window` | `UI.Common/Window.cs:13` | Universal window host: title, content rect, draggable, resizable, persistence hooks |
| `UI.Common.WindowManager` | `UI.Common/WindowManager.cs:12` | Singleton (`WindowManager.Shared`); enumerates, hit-tests, closes-all, presents `Alert` |
| `UI.ProgrammaticWindowCreator` | `UI/ProgrammaticWindowCreator.cs:18` | Bootstrap: instantiates each `IBuilderWindow` as a child of `WindowManager` |
| `UI.IBuilderWindow` / `IProgrammaticWindow` | `UI/IBuilderWindow.cs`, `IProgrammaticWindow.cs` | Marker interface set / constructor protocol |
| `UI.Builder.UIPanelBuilder` | `UI.Builder/UIPanelBuilder.cs:19` | Fluent DSL. `AddField`, `AddSlider`, `AddTabbedPanels`, `VStack`, scroll views |
| `UI.Builder.UIPanel` | `UI.Builder/UIPanel.cs:11` | Backing struct of the builder; owns child panels, KVO observers, rebuild timer |
| `UI.Builder.UIBuilderAssets` (ScriptableObject) | `UI.Builder/UIBuilderAssets.cs:12` | Shared prefab catalog: title, fieldRow, buttons, controls, scroll views |
| `UI.CarInspector.CarInspector` | `UI.CarInspector/CarInspector.cs:27` | The vehicle inspector. Tabbed: Car / Equipment / Passenger / Operations |
| `UI.EngineControls.LocomotiveControlsUIAdapter` | `UI.EngineControls/LocomotiveControlsUIAdapter.cs:20` | The HUD ("LocoControlsUI"). Mode dropdown + control set swap |
| `UI.Common.ModalAlertController` | `UI.Common/ModalAlertController.cs:10` | Singleton modal presenter (Yes/No, input, button strip) |
| `UI.Common.Toast` / `UI.Common.ModalAlert` | `UI.Common/Toast.cs`, `ModalAlert.cs` | Toast popup + the modal it presents |
| `UI.Common.WindowPersistence` (static) | `UI.Common/WindowPersistence.cs:9` | PlayerPrefs-backed window position/size persistence |
| `Game.Settings.CanvasSettingsApplicator` | `Game.Settings/CanvasSettingsApplicator.cs:9` | Subscribes to `CanvasScaleChanged`; sets `CanvasScaler.scaleFactor` |
| `Game.Preferences.GraphicsCanvasScale` | `Game/Preferences.cs:370` | The UI scale setter. `PlayerPrefs("gfx.canvas.scale")`. Sends `CanvasScaleChanged` |
| `UI.ConsistInspector.ConsistInspectorPanel` | `UI.ConsistInspector/ConsistInspectorPanel.cs:6` | **Stub.** "Not implemented." Don't clone — see gotchas |

---

## Spine: how a vanilla window comes alive

```
ProgrammaticWindowCreator.Start()                      ← UI/ProgrammaticWindowCreator.cs:24
   │  Instantiate(windowPrefab) under self
   │  AddComponent<TWindow>() (e.g., CarInspector)
   │  TWindow.BuilderAssets = builderAssets            ← shared ScriptableObject
   │  Window.SetInitialPositionSize(id, size, pos)     ← restores from PlayerPrefs
   │  Window.CloseWindow()                             ← hidden by default
   ▼
TWindow.Show(...) (static, found via WindowManager.GetWindow<T>())
   │  TWindow.Populate()
   │     UIPanel.Create(_window.contentRectTransform, BuilderAssets, BuildClosure)
   │        UIPanelBuilder builder = new(container, assets, panel);
   │        BuildClosure(builder) → AddTitle/AddTabbedPanels/AddField/...
   │  Window.ShowWindow()                              ← SetActive(true), ClampToParent, OrderFront
   ▼
Live panel:
   - TextUpdater coroutines repaint .text every 0.1s (Fast) or 1s (Periodic)
   - SliderUpdater / ToggleUpdater poll their value closure each frame
   - KVO `Observe()` subscriptions trigger panel.Rebuild on key changes
   - RebuildOnEvent<T> registers GalaSoft Messenger handler that rebuilds
```

**`UIBuilderAssets` is a singleton ScriptableObject** held by `ProgrammaticWindowCreator` and forwarded to every window via the `IBuilderWindow.BuilderAssets` setter. Mods that want to reuse vanilla's prefab look (field rows, sliders, dropdowns, scroll rects) can grab the shared assets via `WindowManager.Shared.GetComponent<...>()` chain — but most mods build their own prefab pack.

### Where `Window` lives in the scene graph

`WindowManager` is a `MonoBehaviour` in the `Canvas - HUD` (or sibling) hierarchy under `DontDestroyOnLoad`. Every child `Window` is a sibling under it; `WindowManager.HitTest(mousePosition)` walks the children to find the topmost. The `Toast` and `ModalAlert` instances are **not** under `WindowManager` — they're separate canvases. `WindowManager.OnEnable` calls `CloseAllWindows()` so windows always start hidden across scene reloads.

---

## `UI.Common.Window` — universal window host

Generic prefab that wraps title bar + drag handle + content rect + optional resize grip. Every `IBuilderWindow` (CarInspector, Preferences, Company, Time, …) is a `Component` *added at runtime* to an instance of this prefab.

### Surface

```csharp
public TMP_Text titleLabel;
public RectTransform contentRectTransform;        // YOUR builder root
[SerializeField] private PanelResizer resizer;
[SerializeField] private DraggablePanel draggablePanel;
public Action DelegateRequestClose;               // override Esc-close behavior
public bool IsShown { get; private set; }
public string Title { get; set; }                 // mirror of titleLabel.text
public Vector2 InitialContentSize { get; private set; }

public event Action<bool>    OnShownWillChange;   // BEFORE coroutine flips active
public event Action<bool>    OnShownDidChange;    // AFTER  active flip
public event Action<Vector2> OnDidResize;
public event Action<Vector2> OnDidPosition;

public void  ShowWindow();
public void  CloseWindow();
public void  HandleRequestCloseWindow();          // calls Delegate or CloseWindow
public void  OrderFront();                        // SetAsLastSibling
public void  SetResizable(Vector2 min, Vector2 max);
public void  SetPosition(Position p);
public void  SetPositionRestoring(Vector2 p);
public void  SetContentSize(Vector2 size);
public void  SetContentWidth(int w);              // resize horizontally only
public void  SetContentHeight(int h);
public Vector2 GetContentSize();
public Vector2 GetPosition();
public void  UpdateContentSizeFixedHorizontal();  // for VScrollView panels with no preset height
public void  FireDidResize(Vector2 sizeDelta);    // PanelResizer pokes this
```

`Position` enum: `LowerLeft`, `LowerRight`, `UpperLeft`, `UpperRight`, `Center`, `CenterRight`. `Sizing` is a struct: `Sizing.Fixed(Vector2Int)` or `Sizing.Resizable(min[, max])`.

### Show coroutine quirks

```csharp
private IEnumerator ShowCoroutine(bool shown)
{
    if (!shown) yield return new WaitForEndOfFrame();    // 1-frame defer on close only
    OnShownWillChange?.Invoke(shown);
    UpdateForShown();                                     // SetActive(shown) on every child
    OnShownDidChange?.Invoke(shown);
}
```

**Open is immediate** (no wait); **close defers one frame**. If your mod is going to subscribe to a KVO key while the panel is open and dispose on close, dispose in `OnShownWillChange`, not `DidChange`, otherwise you'll observe the final state writes that happen during the deferred-close frame.

### `UpdateForShown` zaps every child active flag

`UpdateForShown` iterates **direct** children of the window's RectTransform and forces `SetActive(IsShown)` on each (with a special carve-out for `resizer.gameObject`). If you parent a sibling control directly under the `Window` GameObject, it will be force-toggled with the window. Sibling controls should live *under `contentRectTransform`* or be reparented under a non-toggled holder.

### Patch candidates

| Method | Why patch |
|---|---|
| `Window.ShowWindow` | Inject your sibling-panel show. Use a postfix; `OrderFront` already ran. |
| `Window.UpdateForShown` | Wedge in additional active-flag controls if you need bypass logic. Risky — runs on every show/hide. |
| `Window.SetContentSize` | Veto user-resizes (e.g., enforce content-driven sizing). |

---

## `UI.IBuilderWindow` / `IProgrammaticWindow` — the window protocol

```csharp
public interface IBuilderWindow {
    UIBuilderAssets BuilderAssets { get; set; }           // injected post-Awake
}

public interface IProgrammaticWindow : IBuilderWindow {
    string  WindowIdentifier   { get; }                   // PlayerPrefs key suffix
    Vector2Int DefaultSize     { get; }
    Window.Position DefaultPosition { get; }
    Window.Sizing  Sizing      { get; }
}
```

Two flavors of registration in `ProgrammaticWindowCreator`:

```csharp
// Static registration (size/pos passed to creator, identifier == string param)
CreateWindow<UI.CarInspector.CarInspector>("CarInspector", 400, 320, Position.LowerRight);

// Self-describing (the window provides its own metadata)
CreateWindow<TimeWindow>();                                // implements IProgrammaticWindow
```

### Vanilla `IBuilderWindow` registry

| Window | Identifier | Default size | Default position | Resizable? |
|---|---|---|---|---|
| `CompanyWindow` | `"Company"` | 880×600 | Center | No |
| `PreferencesWindow` | `"Preferences"` | 400×400 | Center | 400×400 → 600×800 |
| `BindingsWindow` | `"Bindings"` | 500×500 | Center | No |
| `CarInspector` | `"CarInspector"` | 400×320 | LowerRight | No |
| `CarCustomizeWindow` | `"CarCustomize"` | 400×320 | Center | No |
| `LostCarPlacerWindow` | `"LostCarPlacer"` | 400×320 | Center | No |
| `GuideWindow` | `"Guide"` | 900×600 | Center | 600×400 → 1200×1200 |
| `StationWindow` | `"Station"` | 700×500 | Center | No |
| `TimeWindow` | `"Time"` | 300×150 | UpperRight | Fixed |
| `TimetableWindow` | self-described | — | — | — |
| `TimetableEditorWindow` | self-described | — | — | — |
| `EquipmentWindow` | `"Equipment"` | 800×600 | Center | No |
| `InteractiveBookWindow` | self-described | — | — | — |

**Other windows that don't go through this creator** (hand-instantiated or scene-placed):

- `MapWindow` — registered via `WindowManager.GetWindow` but `Start()` self-instantiates with `SetInitialPositionSize` (id `"Map"`, 600×500, UpperLeft, resizable).
- `EngineRosterPanel` — same pattern (id `"EngineRoster"`, 560×150, LowerRight, resizable).
- `SwitchListPanel`, `Console` (`UI.Console.Console`) — scene-placed `Window` host, configured in `Start`/`Awake`.
- `PauseMenu` — *not* a `Window`; toggles a top-level Canvas via `SetActive`. Static `PauseMenu._paused` tracks state.
- `MainMenu` / `LoadGameMenu` / `MultiplayerJoinMenu` / `MultiplayerHostMenu` etc. — `SoftMenu`/`INavigationView` pattern, only live in main-menu scene.
- `NoticeManager` — top-of-screen "PostEphemeral" notice rows, not a Window.
- `Toast` — single shared instance, separate canvas.
- `TutorialManager` — separate prefab.

### Patch candidates

| Method | Why patch |
|---|---|
| `ProgrammaticWindowCreator.Start` | Postfix to register your own `IBuilderWindow`-implementing component. Means your window joins the `WindowManager.GetWindow<T>` registry, gets persistence, etc. |
| `ProgrammaticWindowCreator.CreateWindow<T>` | Patch generic to inject mod-specific configuration after the vanilla configure. |

---

## `UI.Builder.UIPanel` & `UIPanelBuilder` — the fluent panel DSL

Two-layer system: `UIPanel` (the persistent state object — child panels, observers, rebuild timer) + `UIPanelBuilder` (a per-Rebuild *struct* that exposes the construction API). You **never instantiate a builder directly**; `UIPanel.Create(container, assets, closure)` takes a closure and feeds a builder into it.

### The `UIPanel` lifecycle

```csharp
public static UIPanel Create(RectTransform container, UIBuilderAssets assets, Action<UIPanelBuilder> closure)
{
    var p = new UIPanel(null, container, assets, closure);
    p.Rebuild();
    return p;
}

internal void Rebuild()
{
    DisposeChildren();                  // dispose child UIPanels, their observers, their timers
    _container.DestroyChildren();       // raze the GameObject tree under the container
    _buildClosure(new UIPanelBuilder(_container, _assets, this));
    InvokeOnRebuild();                  // fires OnRebuild + bubbles to parent
}
```

**Every `Rebuild` razes and reconstructs the entire panel.** This is the perf pain point the user's UI Toolkit experiment was reacting to. The pattern *works* for inspectors that change sometimes, and is OK because the constructed control trees are small (~tens of GameObjects); it's bad for per-tick updates with hundreds of cells.

### Triggering a rebuild

```csharp
public void RebuildOnEvent<T>();                          // GalaSoft Messenger handler
public void RebuildOnInterval(float seconds);             // Timer component
public void AddObserver(IDisposable disposable);          // KVO subscription, dispose on next Rebuild
public event Action OnRebuild;
```

For "live" values that should update *without* a full rebuild, the builder offers `AddLabel(Func<string>, Frequency)`, `AddSlider(Func<float>, ...)`, `AddToggle(Func<bool>, ...)` — these add a `TextUpdater` / `SliderUpdater` / `ToggleUpdater` MonoBehaviour that polls the closure on a coroutine and pokes the bound TMP_Text/Slider/Toggle in place. This is the **only** vanilla mechanism for sub-rebuild updates.

### `Frequency` (for poll-based updaters)

```csharp
public enum Frequency { Fast, Periodic }                  // 0.1s, 1.0s
```

Used as the trailing arg to `AddLabel(Func<string>, Frequency)` and `AddField(string, Func<string>, Frequency)`.

### Builder surface (high-traffic methods)

```csharp
// Sections + titles
void AddTitle(string title, string subtitle);
void AddSection(string title);
void AddSection(string title, Action<UIPanelBuilder> closure, float spacing = 0f);

// Field rows (label : control)
IConfigurableElement AddField(string label, RectTransform control);
IConfigurableElement AddField(string label, Func<string> valueClosure, Frequency);
IConfigurableElement AddField(string label, string value);
IConfigurableElement AddFieldToggle(string label, Func<bool> get, Action<bool> set, bool interactable=true);

// Buttons
IConfigurableElement AddButton(string text, Action a);             // default
IConfigurableElement AddButtonMedium(string text, Action a);
IConfigurableElement AddButtonCompact(string text, Action a);
IConfigurableElement AddButtonCompact(Func<string> text, Action a);
IConfigurableElement AddButtonSelectable(string text, bool selected, Action a);

// Inputs
RectTransform AddLabel(string text);
RectTransform AddLabel(string text, Action<TMP_Text> configure);
RectTransform AddLabel(Func<string> closure, Frequency);
RectTransform AddLabelMarkup(string markup);
RectTransform AddLabelEmptyState(string text);
RectTransform AddTextArea(string text, Action<string> onLink);
RectTransform AddMultilineTextEditor(string text, string placeholder, Action<string> onChange, Action<string> onEnd);
RectTransform AddInputField(string value, Action<string> onApply, string placeholder=null, int? characterLimit=null);
RectTransform AddInputFieldValidated(string value, Action<string> onApply, string regex, string placeholder=null, int? max=null);
RectTransform AddInputFieldReportingMark(string value, Action<string> onApply);   // 6-letter validated
RectTransform AddToggle(Func<bool> get, Action<bool> set, bool interactable=true);
RectTransform AddSlider(Func<float> get, Func<string> textClosure, Action<float> set,
                        float min=0, float max=1, bool whole=false, Action<float> editingEnded=null);
RectTransform AddSliderQuantized(Func<float> get, Func<string> textClosure, Action<float> set,
                                  float increment, float min=0, float max=1, Action<float> editingEnded=null);
RectTransform AddDropdown(List<string>, int sel, Action<int>);
RectTransform AddColorDropdown(List<string>, int sel, Action<int>);
RectTransform AddColorDropdown(string hexColor, Action<string>);
RectTransform AddOptionsDropdown(IReadOnlyList<DropdownMenu.RowData>, Action<int>);
RectTransform AddLocationPicker(string prompt, ..., Action<IndustryComponent>);
RectTransform AddLocationField(string name, IIndustryTrackDisplayable, Action jump);

// Layout
RectTransform HStack(Action<UIPanelBuilder>, float spacing=4);     // produces nested UIPanel
VerticalLayoutGroup VStack(Action<UIPanelBuilder>);                 // ditto
RectTransform AlertButtons(Action<UIPanelBuilder>);                 // platform-aware Cancel ordering
RectTransform ButtonStrip(Action<UIPanelBuilder>, int spacing=8);
RectTransform VScrollView(Action<UIPanelBuilder>, RectOffset padding=null);
RectTransform HScrollView(Action<UIPanelBuilder>, RectOffset padding=null);
RectTransform HVScrollView(Action<UIPanelBuilder>, RectOffset padding=null);
RectTransform Spacer();         // flexible
void          Spacer(float size);
void          AddExpandingVerticalSpacer();
RectTransform AddVRule();
RectTransform AddHRule();

// Composite
void AddTabbedPanels(UIState<string> selectedTab, Action<UITabbedPanelBuilder>);
void AddListDetail<TValue>(IEnumerable<ListItem<TValue>>, UIState<string> selected,
                            Action<UIPanelBuilder, TValue> closure, float? listWidth=null);
void AddTable(List<TableRow> rows, List<float> colWidths, TableBuilderConfig config);
void AddBuilderPhoto(string carIdentifier);
void AddInputBindingControl(InputAction inputAction, bool conflict, Action onRebind);
void LazyScrollList(List<object> data, string cellPrefabName);

// Re-render hooks (forward to UIPanel)
void RebuildOnEvent<T>();
void RebuildOnInterval(float seconds);
void Rebuild();
void AddObserver(IDisposable disposable);
```

### Tooltip

`IConfigurableElement.Tooltip(title, message)` and `RectTransform.Tooltip(title, message)` (extension at `RectTransformLayoutExtensions.cs:52`). Adds a `UITooltipProvider` (`UI.Tooltips/UITooltipProvider.cs:6`) MonoBehaviour to the rect — `GameInput.IsMouseOverUI` walks parents looking for it.

### Patch candidates

| Method | Why patch |
|---|---|
| `UIPanel.Rebuild` | Postfix to instrument every rebuild. Useful for measuring rebuild frequency — vanilla CarInspector under load can rebuild several times/sec. |
| `UIPanelBuilder.AddTabbedPanels` | Inject a custom tab into any panel that uses tabbed structure (CarInspector, CompanyWindow, Preferences). Wrap the closure to call yours after vanilla's. |
| `UIPanelBuilder.AddField` overloads | Universal trace point — every field-row goes through one of these three. |
| `BuilderExtensions.AddConditionField` / `AddMileageField` | Replace condition/mileage rendering. See [Wear › patch candidates](wear-durability.md#patch-candidates). |
| `BuilderExtensions.AddRepairDestination` | Where the "Overhaul" repair option is hidden when `WearFeature` is off (`BuilderExtensions.cs:170`). |

### Gotchas

- **`UIPanelBuilder` is a `struct`.** Storing one in a field across closures captures by value of the struct. The `_panel` reference inside is a class so closures still mutate the right tree, but be aware: `builder.Spacing = 5f` assigns to a copy unless you take care.
- **`AddTabbedPanels` consumes a `UIState<string>`.** That state is the *single* source of truth for which tab is open. If you re-create the panel without re-using the same `UIState` instance, the active tab resets to null.
- **`UIState<T>` is just `class { T Value; }`** (`UIState.cs`) — no events, no observers. It's a mutable cell to share across closures, nothing more. Don't put non-trivial state behind it expecting reactivity.
- **The `_container.DestroyChildren()` step in `Rebuild` is unconditional.** If you postfix `Rebuild` and create new UI inside the container, vanilla destroys it on the next rebuild. Parent your additions to a *sibling* RectTransform.
- **`RebuildOnInterval` adds a Timer MonoBehaviour to the container GameObject.** It survives child disposal and ticks until the panel is disposed. Multiple `RebuildOnInterval` calls on the same panel re-`Configure` the same Timer (the most recent interval wins).
- **`RebuildOnEvent<T>` registers via `Messenger.Default.Register`** and unregisters via `Messenger.Default.Unregister(this)` (the `UIPanel`). So one `Unregister` strips *all* event handlers for that panel — there's no per-event removal.
- **`AddInputBindingControl` static-caches `_rebindOverlay`** — first creation parents it under `WindowManager.Shared`'s Canvas. Mods that use `AddInputBindingControl` from a panel rooted elsewhere may see the overlay show up at the wrong z-order.

---

## `UI.CarInspector.CarInspector` — the vehicle inspector

The canonical "open this car" panel. Lives at LowerRight by default. Single shared instance (`_instance`), fed via static `Show(Car)`. Tabs are dynamic per-car: Car / Equipment / Passenger (only if passenger car) / Operations (only if not a tender).

### Surface

```csharp
[RequireComponent(typeof(Window))]
public class CarInspector : MonoBehaviour, IBuilderWindow
{
    private Window _window;
    private Car    _car;
    private static CarInspector _instance;
    private readonly UIState<string> _selectedTabState = new(null);
    private readonly HashSet<IDisposable> _observers = new();
    public UIBuilderAssets BuilderAssets { get; set; }     // injected by ProgrammaticWindowCreator
    public static void Show(Car car);                      // CarInspector.cs:65
    internal static Car ShownCar();                        // returns null if hidden
}
```

### Per-tab builder methods

```csharp
private void PopulatePanel(UIPanelBuilder builder)              // 133
private void PopulateCarPanel(UIPanelBuilder builder)           // 155 — Brake Line, Cylinder, Hand Brake, Cut Out, MU
private void PopulateEquipmentPanel(UIPanelBuilder builder)     // 222 — Condition, Mileage, RepairDest, Sell, Customize
private void PopulatePassengerCarPanel(UIPanelBuilder builder)  // 604 — Stop checklist, Auto Dest
private void PopulateOperationsPanel(UIPanelBuilder builder)    // 305 — TrainCrew, Waybill, Switch List
```

### What CarInspector reads from / writes to

| Source | What | Where |
|---|---|---|
| `Car.air.BrakeLine.Pressure` | Brake line pressure live readout | Car panel, `Frequency.Fast` |
| `Car.air.BrakeCylinder.Pressure` | Cylinder pressure | Car panel |
| `Car.air.handbrakeApplied` | Hand brake state | Car panel |
| `Car.SetBleed()` | Bleed valve action | Car panel button |
| `Car.SetHandbrake(bool)` | Hand brake toggle | Car panel button |
| `Car.ControlProperties[Control.Mu/CutOut]` | MU/CutOut for locos | Car panel toggles |
| KVO `PropertyChange.Control.Handbrake` | Observed for rebuild | `Car.cs:1675` (handbrake key) |
| `Car.Condition`, `Car.RepairCap`, `Car.HasHotbox`, `Car.IsDerailed` | Status text | Equipment panel via `BuilderExtensions.AddConditionField` |
| `Car.OdometerService`, `Car.LastOverhaulOdometer`, `Car.Condition` | Overhaul-due text | Equipment panel via `AddMileageField` |
| `RepairTrack.CalculateRepairWorkOverall(car)` | Repair estimate | Equipment panel |
| `Car.GetWaybill(OpsController.Shared)` | Waybill state | Operations panel |
| KVO `ops.waybill` | Observed → Rebuild | `CarInspector.cs:147` |
| `Car.GetPassengerMarker()` | Passenger destinations | Passenger panel |
| KVO `ops.passengerMarker` | Observed | `CarInspector.cs:671` |
| `Messenger<CarIdentChanged>` | Rebuild on rename | `CarInspector.cs:95` |
| `StateManager.ApplyLocal(...)` messages | `SetPassengerDestinations`, `SetPassengerAutoDestinations`, `SwitchListToggleCarIds`, `SetCarTrainCrew` | Mutations |

### Patch / replace strategy

| Goal | Approach |
|---|---|
| Add a tab ("Mod Foo" tab) | Patch `CarInspector.PopulatePanel` postfix; *insert* a `tabBuilder.AddTab("Foo", "foo", PopulateFooPanel)` call. The closure runs inside vanilla's lambda — capture `_car` from `this`. **Hazard:** tabs are added per `Populate`, which runs on every `Rebuild`; your hook fires every time, so make it idempotent. |
| Add a row to an existing tab | Patch the per-tab `PopulateCarPanel` / `PopulateEquipmentPanel` / etc. postfix, append `builder.AddField(...)`. Lives below vanilla rows. |
| Replace `AddConditionField` rendering | Patch `BuilderExtensions.AddConditionField` (it's a static extension method — Harmony can target it). |
| Suppress the vanilla CarInspector and present your own | Patch `CarInspector.Show(Car)` prefix to no-op (or to call your own panel) and return `false`. The `_instance.Populate` chain stops. **Recommended for major UI overhauls.** |
| Listen for "user inspected this car" | Patch `CarInspector.Show` prefix or postfix; `Show` is the funnel for *all* inspect actions. |
| Open your own panel alongside vanilla | Subscribe to `Window.OnShownDidChange` on the CarInspector window after `WindowManager.Shared.GetWindow<CarInspector>()` returns. Toggle your sibling panel in step. |

### Gotchas

- **`_instance` is set lazily** in `Show()` (`CarInspector.cs:67`). The first `Show` after game-load runs `FindObjectOfType<CarInspector>()`. If you patch and need an early reference, use `WindowManager.Shared.GetWindow<CarInspector>()` instead.
- **`PopulatePanel` runs on every `Rebuild`** (which can be triggered by `CarIdentChanged`, by tab switches, by tab content's KVO observers). Tab `AddTab` calls inside lambdas are **not deduped** — every Rebuild reconstructs the entire tab graph. Patches that append a tab will append it once per rebuild because the underlying `TabView` is also fresh-built.
- **The `_observers` HashSet is disposed *and re-populated* on `Populate`.** It's cleared at line 124-127, then re-added during the build closures. If you add observers from a patch, push them into `_observers` so vanilla disposes them on Populate.
- **`OnEnable` registers `CarIdentChanged`; `OnDisable` unregisters all.** If you patched `OnEnable` to add your own Messenger handler, vanilla's `OnDisable` does `Messenger.Default.Unregister(this)` which removes *yours too* (handler key is the registrant `this`). Use a different `this` (e.g., a child ScriptableObject) to keep yours alive.
- **The "Operations" tab is omitted for tenders** (`CarInspector.cs:144`). Don't assume the tab exists.
- **`Show` does not check `IsShown`** — calling it twice in a frame triggers two `Populate`s. Idempotent against same-Car for `_selectedTabState` reset (line 119-122) but still double-builds.
- **Window position id is `"CarInspector"`** — restoring window state via PlayerPrefs uses that key.

---

## `UI.EngineControls.LocomotiveControlsUIAdapter` — the HUD ("LocoControlsUI")

Bottom-left HUD panel. **Not a `Window`** — a hand-rolled prefab (`LocoControlsUI`) wired with SerializeFields. The `LocomotiveControlsUIAdapter` MonoBehaviour pokes `nameLabel`, `infoALabel`, `infoBLabel`, `speedLabel`, the mode dropdown, and swaps among five `EngineControlSetBase` children based on `AutoEngineerMode`.

### Surface

```csharp
[SerializeField] private TMP_Text nameLabel;
[SerializeField] private TMP_Text infoALabel;
[SerializeField] private TMP_Text infoBLabel;
[SerializeField] private TMP_Text speedLabel;
[SerializeField] private TMP_Dropdown modeDropdown;        // "Manual" / "AE Road" / "AE Yard" / "AE Waypoint"
[SerializeField] private DropdownMenu optionsDropdown;     // gear icon
[SerializeField] private RectTransform controls;
[SerializeField] private EngineControlSetBase manualControls;
[SerializeField] private EngineControlSetBase simplifiedControls;
[SerializeField] private EngineControlSetBase aiRoadControls;
[SerializeField] private EngineControlSetBase aiYardControls;
[SerializeField] private EngineControlSetBase aiWaypointControls;
```

### Update loops

```csharp
private void OnEnable() {                                          // 84
    _coroutine = StartCoroutine(UpdateLocomotiveTextCoroutine());  // 1Hz infoA/infoB
    _controlSets = [manual, simplified, aiRoad, aiYard, aiWaypoint];
    modeDropdown.ClearOptions();
    modeDropdown.AddOptions(["Manual", "AE Road", "AE Yard", "AE Waypoint"]);
    UpdateForSelectedCar();
    Messenger.Default.Register<SelectedCarChanged>(this, _ => UpdateForSelectedCar());
    Messenger.Default.Register<CarIdentChanged>(this, _ => UpdateForSelectedCar());
}

private IEnumerator UpdateSpeedCoroutine() {                       // 247
    var wait = new WaitForSecondsRealtime(0.1f);
    while (true) {
        float mph = TrainController.Shared.SelectedLocomotive.VelocityMphAbs;
        int n = (mph >= 1f) ? Mathf.RoundToInt(mph) : (mph > 0.1f ? 1 : 0);
        speedLabel.SetText("<mspace=0.55em>{0}</mspace>\n<color=#5D5B55><size=20%>MPH</size></color>", n);
        yield return wait;
    }
}
```

The selected locomotive is `TrainController.Shared.SelectedLocomotive` (which is whatever `TrainController.Shared.SelectedCar` points at, or null if no loco selected). Switching cars fires `Messenger<SelectedCarChanged>` and re-runs `UpdateForSelectedCar`.

### Mode-driven control swap

```csharp
private EngineControlSetBase SelectedControlSet(AutoEngineerMode mode) => mode switch {
    AutoEngineerMode.Off      => SimplifiedControls ? simplifiedControls : manualControls,
    AutoEngineerMode.Road     => aiRoadControls,
    AutoEngineerMode.Yard     => aiYardControls,
    AutoEngineerMode.Waypoint => aiWaypointControls,
};
```

`SimplifiedControls` is `Preferences.SimplifiedControls` (PlayerPrefs). Toggled via the gear-icon options dropdown when `AutoEngineerMode.Off`. The five control set MonoBehaviours all derive from `EngineControlSetBase` (`UI.EngineControls/EngineControlSetBase.cs`) and are scene-placed siblings under the HUD prefab.

### `LocomotiveControlsHoverArea` — the "5 cars, 350 tons, 280 ft" overlay

Sibling MonoBehaviour (`UI/LocomotiveControlsHoverArea.cs:13`). On hover, shows a summary string (`SummaryText()`) computed from `TrainController.Shared.SelectedTrain`. The `infoALabel` of the HUD also displays this same string (`LocomotiveControlsUIAdapter.cs:267`). Coroutine alpha-lerps the bg image on cursor enter/exit at 0.1s polling.

### Patch / replace strategy

| Goal | Approach |
|---|---|
| Add a control row above/below vanilla controls | The HUD is a single-prefab tree; appending controls at runtime requires re-parenting under `controls` and rebuilding sibling order. Practical, but every `EngineControlSetBase` instance sets up its own children — pick a sibling rect, not inside one of the five sets. |
| Hide vanilla and replace | Disable `LocomotiveControlsUIAdapter.gameObject`, instantiate your own HUD prefab as a sibling. The user's experiment did exactly this. |
| Add a sibling panel above the HUD | Anchor your panel to BottomLeft just above LocoControlsUI's height (e.g., +160 y). Subscribe to `CanvasScaleChanged` to rescale the same way as `CanvasSettingsApplicator` if your canvas isn't already wired. |
| Read what HUD knows | `TrainController.Shared.SelectedLocomotive` (BaseLocomotive), `TrainController.Shared.SelectedCar` (Car), `TrainController.Shared.SelectedTrain` (IEnumerable<Car>), `AutoEngineerPersistence.Orders` for current AE mode. |

### Patch candidates (the three you'd actually use)

| Method | Why patch |
|---|---|
| `LocomotiveControlsUIAdapter.UpdateForSelectedCar` | Postfix to refresh your sibling HUD when the selected loco changes. |
| `LocomotiveControlsUIAdapter.UpdateSelectedControlSet(Orders)` | Catch every AE mode change. Includes `Off`/`Road`/`Yard`/`Waypoint`. |
| `LocomotiveControlsUIAdapter.UpdateCarText` | Catch the 1Hz info text refresh — convenient tick for sibling-panel updates. |

### Gotchas

- **The HUD is not a `Window`** — `WindowManager.GetWindow<LocomotiveControlsUIAdapter>()` returns null. Find it via `FindObjectOfType<LocomotiveControlsUIAdapter>()`.
- **`SimplifiedControls` is read from `Preferences` on every `UpdateSelectedControlSet`** (`PlayerPrefs.GetInt("ae.simplified", 0)`). No event fires when this changes — the gear-icon dropdown calls `UpdateSelectedControlSet` directly.
- **Speed label uses `<mspace>` rich text** for monospace digits. If you reuse the format string, `<mspace=0.55em>` is sized to roughly match TMP's default font; differs per font choice.
- **The `infoBLabel` includes timetable train name.** `TimetableController.Shared.TryGetTrainForTrainCrew(...)` is the lookup; if no timetable, just shows crew name; if no crew, infoBLabel is null/empty.
- **`UpdateLocomotiveTextCoroutine` is started in `OnEnable` and never cancelled correctly** — vanilla calls `StopCoroutine(_coroutine)` on `OnDisable` (line 105). Confirm if you re-enable: it does call StartCoroutine again, no leak.

---

## `UI.ConsistInspector.ConsistInspectorPanel` — the **stub**

```csharp
public class ConsistInspectorPanel : MonoBehaviour
{
    public static ConsistInspectorPanel Shared { get; private set; }
    public void Present();   // -> Rebuild
    public void Hide();
    public void OnReloadPressed();
    private void Rebuild() {
        RemoveAllCells();
        titleLabel.text = "Not implemented";       // ← LITERALLY SAYS "Not implemented"
    }
}
```

`ConsistInspectorCell` exists as a sibling stub (sets `gameObject.SetActive(false)` in `Awake`). **There is no functional consist inspector in vanilla** — the user's experiment cloned this dead prefab by mistake. **Do not use as a reference for any production work.** The inspector you actually want to clone for a per-car-row UI is `EngineRosterPanel` (lazy scroll list with cells per locomotive) or build your own from `UIPanelBuilder.LazyScrollList(data, cellPrefabName)`.

### Patch candidates

None worth listing. The component does nothing.

### Why it ships

Likely a placeholder for a feature shelved post-prototype. The `Shared` singleton is set in `Awake` so other code can call `Present()` and "open" the empty panel — none of vanilla calls it, so the panel never appears. **Modders can repurpose by patching `Rebuild` to build their own UI**, but it's cleaner to just create your own MonoBehaviour with its own prefab.

---

## `UI.PreferencesWindow.PreferencesWindow` & `PreferencesBuilder` — the settings panel

In-game Preferences window (Esc menu → Preferences, or top-right area). 5 tabs: Character / Graphics / Sound / Input / Features.

### Surface

```csharp
[RequireComponent(typeof(Window))]
public class PreferencesWindow : MonoBehaviour, IBuilderWindow
{
    public static PreferencesWindow Instance => WindowManager.Shared.GetWindow<PreferencesWindow>();
    public static void Toggle();
    public static void Show();
}

public static class PreferencesBuilder
{
    public static void Build(UIPanelBuilder builder);            // entry; calls AddTabbedPanels
}
```

### Tabs

| Tab | TabId | Content (high level) |
|---|---|---|
| Character | `"char"` | `CharacterSettingsBuilder.BuildCharacterPanel` — avatar customization |
| Graphics | `"gfx"` | Resolution, fullscreen, **UI Scale** (slider), VSync, Quality, Particles, Draw distance, Tree/Detail density, FOV, exposure/contrast |
| Sound | `"sound"` | Main, Engine, Whistle, Bell, Dynamo, Wheels, Environment, CTC Bell — all `Preferences.SoundVolume*` sliders |
| Input | `"input"` | Mouse look mode, speed, invert; "Customize Bindings" button → `BindingsWindow` |
| Features | `"features"` | Sway intensity, Compass toggle, Always Show Clock, Car Update Optimization (experimental), Analytics opt-in |

### UI Scale (cross-cutting)

```csharp
// PreferencesBuilder.cs:122-130
_uiScaleValue = Preferences.GraphicsCanvasScale;
builder.AddField("UI Scale", builder.AddSliderQuantized(
    () => _uiScaleValue,
    () => $"{Mathf.Round(_uiScaleValue * 100f)}%",
    f => _uiScaleValue = f,                        // intermediate value; not yet applied
    increment: 0.05f,
    min:       0.75f,
    max:       CanvasSettingsApplicator.MaxCanvasScale(),
    editingEnded: f => {
        _uiScaleValue = f;
        Preferences.GraphicsCanvasScale = _uiScaleValue;   // commit on slider release
    }));
builder.RebuildOnEvent<CanvasScaleChanged>();
```

Setting `Preferences.GraphicsCanvasScale` writes to `PlayerPrefs("gfx.canvas.scale")` and fires `CanvasScaleChanged` (Messenger). `CanvasSettingsApplicator` (one per `Canvas` with the component) updates its `CanvasScaler.scaleFactor`. The Preferences panel itself rebuilds on the same event.

### `Game.Preferences` PlayerPrefs keys (the ones UI-relevant)

| Pref | Key | Default | Event |
|---|---|---|---|
| `GraphicsCanvasScale` | `gfx.canvas.scale` | 1.0 | `CanvasScaleChanged` |
| `MouseLookToggle` | `input.mouselook.toggle` | false | — |
| `MouseLookSpeed` | `input.mouselook.speed` | 1.0 | — |
| `MouseLookInvert` | `input.mouselook.invert` | false | — |
| `SimplifiedControls` | `ae.simplified` | false | — |
| `ShowCompass` | `ui.compass` | true | `UISettingDidChange` |
| `ShowClockAlways` | `ui.clock.always` | false | `UISettingDidChange` |
| `CameraSwayIntensity` | `cam.sway.intensity` | 1.0 | — |
| `EnableCarUpdateOptimization` | `gameplay.car-update-opt` | false | — |
| `Analytics` | `analytics.optin` | OptOut | `AnalyticsPreferenceDidChange` |
| (sound volumes) | `sound.vol.*` | 1.0 | `SoundVolumeChanged` |

### Patch candidates

| Method | Why patch |
|---|---|
| `PreferencesBuilder.Build` | Postfix to add a 6th tab. Same `_selectedTabState` is shared. |
| `PreferencesBuilder.BuildTabFeatures` | Append toggles/sliders for mod prefs. |
| `PreferencesBuilder.BuildTabGraphics` | If you want to replace UI Scale handling (e.g., separate HUD vs window scales). |
| `Preferences.GraphicsCanvasScale` setter | Intercept all UI-scale writes to apply your own scaling logic. |

---

## `UI.CompanyWindow.CompanyWindow` & `BuilderExtensions` — the omnibus admin window

The "Company" window (Esc → Company icon top-right) is an 880×600 tabbed list-detail panel. 8 tabs: Railroad, Locations, Milestones (Company mode only), Finance, Equipment, Employees, Crews, Settings.

### Tabs

```csharp
tabBuilder.AddTab("Railroad",   "railroad",   RailroadPanelBuilder.Build);
tabBuilder.AddTab("Locations",  "locations",  b => LocationsPanelBuilder.Build(b, _selectedLocationsItem));
if (Company mode) AddTab("Milestones", "milestones", b => GoalsPanelBuilder.Build(b, _selectedGoalsItem));
tabBuilder.AddTab("Finance",    "finance",    FinancePanelBuilder.Build);
tabBuilder.AddTab("Equipment",  "equipment",  b => EquipmentPanelBuilder.Build(b, _selectedCarItem));
tabBuilder.AddTab("Employees",  "employees",  b => EmployeesPanelBuilder.Build(b, _selectedPlayerId));
tabBuilder.AddTab("Crews",      "crews",      b => CrewsPanelBuilder.Build(b, _selectedTrainCrewId));
tabBuilder.AddTab("Settings",   "settings",   b => SettingsPanelBuilder.Build(b, _selectedSettingsItem));
```

### `BuilderExtensions` (the per-Car helpers)

`UI.CompanyWindow/BuilderExtensions.cs` — a single static class of `UIPanelBuilder` extension methods that compose larger Car-specific row groups. Cross-cuts CarInspector + CompanyWindow's Equipment tab.

| Method | Purpose | File:Line |
|---|---|---|
| `AddConditionField(this UIPanelBuilder, Car)` | Condition % + warnings (RepairCap, Hotbox, Derailed) | `BuilderExtensions.cs:23` |
| `AddMileageField(this UIPanelBuilder, Car)` | Overhaul-due text (or actual miles for non-owned cars) | `BuilderExtensions.cs:49` |
| `AddTrainCrewDropdown(this UIPanelBuilder, ..., Action<string>)` | Train crew picker | `BuilderExtensions.cs:74` |
| `AddRepairDestination(this UIPanelBuilder, Car)` | Drop-down of RepairTracks; "Overhaul" option only when `Car.WearFeature == true` (`:170`) | `BuilderExtensions.cs:162` |
| `AddSellDestination(this UIPanelBuilder, Car)` | Drop-down of Interchanges | `BuilderExtensions.cs:179` |
| `AddDropdownIntPicker(this UIPanelBuilder, ...)` | Generic int picker | `BuilderExtensions.cs:128` |

### Patch candidates

| Method | Why patch |
|---|---|
| `CompanyWindow.Populate` | Postfix to add a 9th tab via the builder closure (you'll need to re-grab the tab builder — easier to patch `AddTabbedPanels` callsite and inject before `Finish`). |
| `BuilderExtensions.AddConditionField` | Replace condition rendering everywhere. See [Wear › patch candidates](wear-durability.md#patch-candidates). |
| `BuilderExtensions.AddMileageField` | Replace mileage rendering. |
| `BuilderExtensions.AddRepairDestination` | Customize repair destinations or change "Overhaul" gating. |

### Gotchas

- **`ShownPath` returns "tab/sublocation"** — useful for deep-linking back. Patch carefully if you add mod-side tabs.
- **`OnDisable` disposes `_panel`** — but doesn't recreate on re-`Populate`; the next Show always rebuilds.
- **`builder.AddObserver(stateManager.Storage.ObserveTimetableFeature(...))`** triggers a rebuild whenever the timetable feature toggles. Adding observers from a postfix patch on `Populate` means dispose handling is vanilla's responsibility — call `builder.AddObserver` rather than holding a `IDisposable` yourself.

---

## `UI.Common.Toast` & `UI.Common.ModalAlertController` — the popup primitives

### `Toast` (transient, non-blocking)

```csharp
public static void Present(string text, ToastPosition position = ToastPosition.Middle);
```

Single shared instance found via `FindObjectOfType<Toast>()` on first call. Plays a 2.5s LeanTween animation: scale 0.5 → 1 → 0.5, alpha 0 → 1 → 0, anchored at top or middle. Calling `Present` again cancels in-flight tweens and replays. **Not threadsafe; main thread only.**

Used by: `WindowManager.Present(Alert)` for `AlertStyle.Toast` (which is the default `Alert` flow); `CarInspector.ToastPresentCopied`; `Console`. Mods can call `Toast.Present(...)` freely.

### `ModalAlertController` (blocking, button-driven)

Singleton `ModalAlertController.Shared` (set in `Awake`). Two presentation styles:

```csharp
// Quick: title + message + buttons
public static void Present<T>(string title, string message,
                              IEnumerable<(T, string)> buttons,
                              Action<T> onButton);

// With input field
public static void Present<T>(string title, string message, string inputString,
                              IEnumerable<(T, string)> buttons,
                              Action<(T, string)> onButton);

// Convenience
public static void PresentOkay(string title, string message, Action onOkay = null);

// Fully custom: build the modal contents with UIPanelBuilder
public static void Present(Action<UIPanelBuilder, Action> builderDismissClosure, int width = 400);
```

Internally instantiates a `ModalAlert` prefab (`UI.Common/ModalAlert.cs:7`) under `ModalAlertController.canvas`. The dismiss closure is the second argument to your build closure — call it to dismiss. Animation: scale 0.5 → 1 (elastic), alpha fade. Time-scale-independent (so paused-game modals work).

### `RequestSaveReopen` modal example

`SettingsPanelBuilder` uses this when the user toggles `OilFeature` — see [Wear › toggle UI](wear-durability.md#toggle-spine-how-wearfeature-propagates) — to alert "Please save and reopen the game". Standard pattern: `ModalAlertController.PresentOkay("title", "message", onOkay)`.

### Patch candidates

| Method | Why patch |
|---|---|
| `Toast.Present` | Hijack toast presentation (e.g., for translation, log capture). |
| `ModalAlertController._Run` (private) | Universal hook for *all* modal presentations. Patch private member or wrap `Present` overloads. |

### Gotchas

- **Toast `Present` finds the instance lazily.** First call after a scene reload runs `FindObjectOfType<Toast>()`. Will throw if no Toast exists in scene.
- **ModalAlert `Configure` sets `_panel = UIPanel.Create(contentRect, ...)`** — your build closure runs once at present time, never re-runs. To update modal contents you'd need to dispose+recreate. Use a static `UIState` field if needed.
- **The "Cancel" button is reordered platform-aware** by `UIPanelBuilder.AlertButtons` (`UIPanelBuilder.cs:504`) — Cancel is *first* on macOS/Linux, *last* on Windows. Apple-style.

---

## `UI.Map.MapWindow` — the world map

Resizable 600×500 RawImage-based map at UpperLeft. Renders to a per-window `RenderTexture` (recreated on resize). `MapBuilder` populates `MapIcon` / `MapLabel` / `MapSwitchStand` children driven by world data. Click → `IMapClickable.Click()`.

```csharp
[RequireComponent(typeof(Window))]
public class MapWindow : MonoBehaviour
{
    public static void Show();
    public static void Show(Vector3 gamePosition);
    public static void Toggle();
    public void  ClickLocateMe();             // jump to current camera
}
```

### Patch candidates

| Method | Why patch |
|---|---|
| `MapWindow.OnWindowShown` / `_Show` | Hook for "user opened the map." |
| `MapWindow.OnClick` | Customize map-click behavior (the `IMapClickable` dispatch). |
| `MapBuilder.Rebuild` | The icon-population pass — see `map-mods-vanilla-survey.md` for prior research. |

---

## `Game.Settings.CanvasSettingsApplicator` — UI scale plumbing

```csharp
[RequireComponent(typeof(CanvasScaler))]
public class CanvasSettingsApplicator : MonoBehaviour
{
    private void OnEnable()  { Messenger.Default.Register<CanvasScaleChanged>(this, _ => UpdateCanvasScale()); UpdateCanvasScale(); }
    private void OnDisable() { Messenger.Default.Unregister(this); }
    private void UpdateCanvasScale() { _canvasScaler.scaleFactor = Preferences.GraphicsCanvasScale; }
    public  static void  ValidateCanvasScale();
    public  static float MaxCanvasScale() => Mathf.Clamp(Mathf.Floor(Screen.height / 650f / 0.05f) * 0.05f, 0.1f, 2f);
}
```

**Attached to specific Canvases only** — not every Canvas in vanilla scenes has it. The HUD Canvas and the game's primary uGUI Canvas have it; mod-instantiated Canvases do not. If you need to participate in UI scale changes, either add the component to your Canvas or subscribe to `CanvasScaleChanged` directly:

```csharp
Messenger.Default.Register<CanvasScaleChanged>(this, _ => RescaleMyUI());
```

### `MaxCanvasScale` formula

`floor(Screen.height / 650 / 0.05) * 0.05`, clamped to [0.1, 2.0]. So a 1080p screen caps at 1.65×; 1440p at 2.0×; 4K at 2.0× (clamped). The Preferences slider min/max use this dynamically (`PreferencesBuilder.cs:127`).

### Cross-cutting

- Sender: `Preferences.GraphicsCanvasScale` setter (`Game/Preferences.cs:379`).
- Receivers: every `CanvasSettingsApplicator` instance + the Preferences window itself (`PreferencesBuilder.cs:131` `RebuildOnEvent<CanvasScaleChanged>()`).
- Read-only consumers should use `Preferences.GraphicsCanvasScale` directly (it's a `PlayerPrefs.GetFloat("gfx.canvas.scale", 1f)`); no need for indirection.

---

## `UI.Common.WindowManager` & `WindowPersistence`

### WindowManager

```csharp
public class WindowManager : MonoBehaviour
{
    public static WindowManager Shared { get; private set; }      // set in Awake
    private void OnEnable() => CloseAllWindows();                  // every scene-load
    public  void CloseTopmostWindow();
    public  TWindow GetWindow<TWindow>();                          // returns first child component
    public  Window  HitTest(Vector3 mousePosition);                // walks children
    public  void Present(Alert alert);                             // toast or console line
}
```

Children are enumerated in **reverse sibling order** (top of stack first) so `HitTest` finds the front-most window. `OrderFront` (`Window.SetAsLastSibling`) re-stacks.

### WindowPersistence (extension methods)

```csharp
public static void SetInitialPositionSize(this Window window, string identifier,
                                            Vector2 defaultSize, Window.Position defaultPosition,
                                            Window.Sizing sizing);
```

Reads `PlayerPrefs.GetString("window." + identifier)` (JSON-encoded `{Shown, Position, Size}`), restores or applies defaults, then auto-saves on `OnShownDidChange`/`OnDidPosition`/`OnDidResize`. Position is normalized by `Screen.width * GraphicsCanvasScale` so it survives resolution changes.

### Patch candidates

| Method | Why patch |
|---|---|
| `WindowManager.Present(Alert)` | Customize alert routing. |
| `WindowPersistence.SetInitialPositionSize` | Modify default-position policy. The `OnShownDidChange += DoSaveWindow` line means *every* show/hide writes PlayerPrefs — patch if that's noisy. |

---

## `UI.Console.Console` — drop-down console

Uses `Window` for the expanded view, plus a `CollapsedConsole` mini view. Lives at top of screen. Subscribed to `IConsoleChild` events. Receives input via `InputActionAsset` action map `"Console"`.

```csharp
public static Console shared { get; }                       // FindObjectOfType
public event Action<bool>   OnFocusedChanged;               // expanded show/hide
public event Action<string> OnUserInput;                    // user submitted line
public void AddLine(string text, GameDateTime ts);
public void Toggle();
```

Console commands are auto-registered via `[ConsoleCommand]` attribute scanning. See `UI.Console.Commands/` (e.g., `RepairCommand`, `SetLoadCommand`). Vanilla console is a mod-extensible surface but the registry isn't a public API — modders typically use the API mod or define their own commands via the Console mod plugin.

---

## `UI.Tooltips.UITooltipProvider` — universal tooltip

```csharp
public class UITooltipProvider : MonoBehaviour
{
    public Func<TooltipInfo> DynamicTooltipInfo;                  // dynamic: called every tick
    [SerializeField] private string tooltipTitle;                 // static
    [SerializeField] private string tooltipText;
    public TooltipInfo TooltipInfo { get; set; }
    public string Title { get; set; }
}
```

Attached to `RectTransform`s via `Tooltip(title, message)` extension. `GameInput.IsMouseOverUI` walks up the click target's parents looking for `UITooltipProvider`; the result feeds the on-screen tooltip widget. To trigger a tooltip from your sibling-panel mod, just `myRect.AddComponent<UITooltipProvider>()` and set `TooltipInfo`.

---

## `UI.ContextMenu.ContextMenu` — radial right-click menu

Singleton `ContextMenu.Shared` (`UI.ContextMenu/ContextMenu.cs:17`); `ICancelHandler`. Items grouped by `ContextMenuQuadrant` (NE/NW/SE/SW), arranged radially around the cursor with `radius=100`. Show/hide animations via LeanTween (0.15s in / 0.25s out). Used for car/track interactions in the world.

Mods extending right-click behavior typically attach themselves to the relevant `IPickable.ActivationFilter` chain rather than directly into `ContextMenu`.

---

## `UI` — top-of-screen and HUD-adjacent

| Component | File | Role |
|---|---|---|
| `TopRightArea` | `UI/TopRightArea.cs` | The clock + button cluster (Company / Timetable / SwitchList / Profile / Guide / Tutorial / Console / Roster / Purchase / Balance / Time). Hover-fade unless `ShowClockAlways`. |
| `CompassHUD` | `UI/CompassHUD.cs` | Top-edge compass label. Reads `Preferences.ShowCompass`. Alpha-fades based on camera rotation activity. Manages location-indicator callouts. |
| `FPSDisplay` | `UI/FPSDisplay.cs` | F12 toggle. |
| `BalanceDisplay` | `UI/BalanceDisplay.cs` | Top-right money readout. |
| `TimeWindow` | `UI/TimeWindow.cs` | Time multiplier + pause/play. |
| `TrainBrakeDisplay`/`TrainStatDisplay`/`TrainStatTextDisplay` | `UI/TrainBrakeDisplay.cs` etc. | Brake gauge UIs. `TrainStatDisplay` is abstract; subclasses implement `SetGauges(mph, mainRes, eqRes, brakeCyl, brakePipe)`. Fed from `TrainController.Shared.SelectedLocomotive.air` (`LocomotiveAirSystem`). |
| `LocationIndicatorController` | `UI/LocationIndicatorController.cs` | The "callout pin" placement on track spans / industries. Used by `LocationIndicatorHoverArea` and `CompassHUD`. |
| `Hyperlink` | `Hyperlink.cs` | Used by markdown-style text in TMP rich text (`<link>`). `TextLinkReceiver` pairs with it on TMP_Text components. |
| `MarkupTextBox`, `ReleaseNotesTextBox`, `TMPTextMarkupExtensions` | `UI/...` | Extended TMP rich-text surface for in-game prose. |

### Brake displays

`TrainStatDisplay` (`UI/TrainStatDisplay.cs:7`) is the abstract base; concrete subclasses pick which gauges to show. `Update()` reads `TrainController.Shared.SelectedLocomotive`, casts `air` to `LocomotiveAirSystem`, and feeds `velocity, mainResPsi, eqResPsi, brakeCylPsi, brakePipePsi` to `SetGauges`. **Read-only** — no mutation. Mod-side brake HUDs duplicate this pattern.

---

## Messenger events relevant to UI

`GalaSoft.MvvmLight.Messaging` (bundled in Assembly-CSharp). All vanilla events use `Messenger.Default`. Relevant payload-free events:

| Event | Sender | Receivers (UI) |
|---|---|---|
| `Game.Settings.CanvasScaleChanged` | `Preferences.GraphicsCanvasScale` setter | All `CanvasSettingsApplicator`s; `PreferencesBuilder` rebuild |
| `Game.Events.UISettingDidChange` | `PreferencesBuilder` (compass/clock toggles) | `TopRightArea.HandleShowAlwaysChanged`, `CompassHUD.UpdateActive` |
| `Game.Events.SelectedCarChanged` | `TrainController` (when selected car/loco changes) | `LocomotiveControlsUIAdapter.OnEnable` |
| `Game.Events.CarIdentChanged` | `Car.Rename` | `CarInspector`, `LocomotiveControlsUIAdapter` |
| `Game.Events.TrainCrewsDidChange` | `PlayersManager` | `BuilderExtensions.AddTrainCrewDropdown` (via `RebuildOnEvent`) |
| `Game.Events.CarTrainCrewChanged` | `Car.SetCarTrainCrew` | `BuilderExtensions.AddTrainCrewDropdown` |
| `Game.Events.TimetableDidChange` | `TimetableController` | `CarInspector.PopulateOperationsPanel` |
| `Game.Events.SwitchListDidChange` | `SwitchListPanel` | `CarInspector.PopulateWaybillPanel` |
| `Game.Events.PropertiesDidRestore` | `StateManager` after KVO restore | Any UI that needs to wait for state to be live (`TopRightArea`, etc.) |
| `Game.Events.MapWillLoadEvent` / `MapDidLoadEvent` / `MapDidUnloadEvent` | `MapManager` | UI components that hold per-map state |
| `Game.Events.GameModeDidChange` | `StateManager` | UI that varies by sandbox vs. Company |
| `Game.Settings.SoundVolumeChanged` | `Preferences.SoundVolume*` setters | Audio bus updates (not UI-only) |
| `Game.Settings.GraphicsSettingsChanged` | several Preferences setters | `GraphicsSettingsApplicator` |
| `Game.Settings.GraphicsDrawDistanceChanged` | `Preferences.GraphicsDrawDistance` | terrain LOD components |
| `Game.Settings.PostProcessingPreferenceChanged` | `Preferences.GraphicsPostExposure`/`Contrast` | post-process volume |
| `Game.Settings.EnviroSettingChanged` | `Preferences.GraphicsNightLightLevel` | weather system |

For "I want to react to UI scale changes," the canonical pattern is:

```csharp
private IDisposable _scaleObserver;
private void OnEnable() {
    _scaleObserver = null;
    Messenger.Default.Register<Game.Settings.CanvasScaleChanged>(this, _ => RescaleMyUI());
    RescaleMyUI();
}
private void OnDisable() => Messenger.Default.Unregister(this);
```

Or use `CanvasSettingsApplicator` directly on your Canvas if you want `CanvasScaler.scaleFactor` automatic (and you don't multiply pixel sizes manually).

---

## TextMeshPro usage notes

Vanilla uses **TMP UGUI** (`UnityEngine.UI.Text` does not appear in any vanilla UI). Every label is `TMP_Text` (interface) backed by `TextMeshProUGUI` (concrete). Rich text features used in vanilla strings:

- `<sprite name=Foo>` — references TMP's sprite asset (icons like `Warning`, `Flame`, `Copy`, `Coupled`, `CycleWaybills`, `MouseLeft`).
- `<mspace=0.55em>...</mspace>` — monospace digits, used by speed/clock readouts.
- `<color=#5D5B55>...</color>`, `<size=20%>...</size>`, `<b>`, `<size=80%>` — styling.
- `<link="...">...</link>` — paired with `TextLinkReceiver` MonoBehaviour for clickable links (`Hyperlink.cs`, `TMPTextMarkupExtensions.cs`).

The `LegacyRuntime` font is the default; vanilla ships its own TMP font assets in the project. Mods that build TMP labels at runtime can use `TMP_Settings.defaultFontAsset` or grab `_assets.labelControl.font` from `UIBuilderAssets`.

**Always-uppercase labels** (e.g., button text in vanilla buttons) come from the TMP material preset `UPPERCASE` or are hard-coded `.ToUpper()` at the call site — there's no CSS-style `text-transform`. The user's UI Toolkit experiment confirmed this and works around it via `.ToUpper()`.

---

## uGUI vs UI Toolkit

**Vanilla is 100% uGUI + TMP.** The only `UnityEngine.UIElements` reference in `Assembly-CSharp` is `GameInput.IsMouseOverUI` walking `PanelRaycaster.panel.Pick(point)` to detect hover over modder-instantiated UIDocuments. Vanilla never instantiates a `UIDocument` or a `PanelSettings`.

This means:
- Mod UIDocuments **do** participate in `IsMouseOverUI` (so vanilla won't fire mouse-pickable events under your panels).
- Mod UIDocuments **do not** automatically pick up `CanvasScaleChanged` — you must subscribe yourself and apply `panelSettings.scale` (or multiply at construction, which the user's experiment found cleaner).
- There's no shared theme/USS in vanilla to reference. Mods bring their own.

---

## Canvas hierarchy & sortOrder

Vanilla doesn't expose Canvas sortOrder constants in the source — they're set in scene asset. From observation:

- Game scene has multiple Canvases: `Canvas - HUD` (the bottom-left HUD), the WindowManager Canvas (where windows live), the Toast Canvas, ModalAlertController's `canvas` (top-most for blocking modals), CompassHUD canvas, NoticeManager canvas.
- `ContextMenu` self-bumps to `HighSortingLayer = 30000` while shown (`UI.ContextMenu/ContextMenu.cs:49`).
- Modals and toasts are above windows; windows are above HUD; HUD is above tooltips? — order verifiable only at runtime via the user's RuntimeDumper.

**Practical rule:** mods that present new windows should reuse `WindowManager` (so they sort with vanilla windows), or instantiate a sibling Canvas with `sortOrder >= 30001` if they need to overlay context menus (modals do this).

---

## Cross-cutting types used by UI

| Type | File | Note |
|---|---|---|
| `TooltipInfo` (struct) | likely `Helpers/...` | `(string Title, string Text)` pair, `static Empty` |
| `ColorHelper.ColorFromHex(string)` | `Helpers/ColorHelper.cs` | Hex → Color, used by `UIPanelBuilder.CreateRule` (`#4E493E`) |
| `LeanTween` / `LTSeq` | bundled | All vanilla UI animations use it (Toast, ModalAlert, ContextMenu) |
| `GalaSoft.MvvmLight.Messaging.Messenger` | bundled | Universal pub/sub bus |
| `KeyValueObject` | (Core or Game.State) | KVO bus; UI subscribes via `kvo.Observe(key, callback, callInitial)` |
| `IConfigurableElement` | `UI.Builder/IConfigurableElement.cs` | Returned by builder methods for fluent `.Tooltip()`, `.Disable()`, `.Width()`, `.FlexibleWidth()` chaining |
| `ConfigurableElement` | `UI.Builder/ConfigurableElement.cs` | Concrete impl wrapping a `RectTransform` |
| `RectTransformLayoutExtensions` | `UI.Builder/RectTransformLayoutExtensions.cs` | `Width`, `Height`, `FlexibleWidth`, `FlexibleHeight`, `ChildAlignment`, `Tooltip`, `SetTextMarginsTop` |
| `LayoutGroupExtensions` / `LayoutGroupFluentExtensions` | `UI.Builder/LayoutGroup*.cs` | Fluent `.Padding(...)`, `.ChildAlignment(...)` |

---

## Gotchas (cross-cutting)

- **`UIPanel.Rebuild` razes everything.** Per-tick rebuilds at scale (e.g., a CarInspector left open while many KVO keys change) cost real frame time. Vanilla CarInspector mitigates by using `TextUpdater` polling for high-frequency values (brake line PSI) and only Rebuild-ing on coarse events (waybill change, passenger marker change). Mod inspectors that Rebuild on every per-tick value change will stutter.
- **`PlayerPrefs` writes happen on every show/hide/move/resize** of any persisted window. PC PlayerPrefs is registry-backed; this isn't free. If you persist your own windows, debounce writes if you find them in profiling.
- **`Window.UpdateForShown` zaps direct children's active flag.** Don't parent helpers under the Window root — put them inside `contentRectTransform` or a sibling.
- **`OnDisable` on `OnEnable`-registered Messenger handlers calls `Unregister(this)` which removes ALL handlers for that owner.** This is GalaSoft API behavior. Mods that postfix `OnEnable` to add a handler with the same `this` lose it on `OnDisable`. Use a different owner key.
- **`UIBuilderAssets` is a ScriptableObject reference.** Assigning to fields (e.g., a different `labelControl` prefab) on it would affect *every* window globally. Don't mutate; instantiate copies if you need different prefabs.
- **`AddTrainCrewDropdown(this UIPanelBuilder, Car car)`** is overloaded — the no-args version (`bool` return) automatically calls `RebuildOnEvent<CarTrainCrewChanged>` and `RebuildOnEvent<TrainCrewsDidChange>`. Patches that postfix it run *after* those event subscriptions are added.
- **`ProgrammaticWindowCreator` runs in `Start`** — windows aren't instantiated until then. Mods accessing `WindowManager.Shared.GetWindow<...>` before that throws `ArgumentException`. Defer to a coroutine or `Update` first-frame guard.
- **`SetContentSize` clamps to screen / GraphicsCanvasScale.** Very large windows on small screens will be squished. The clamp is silent.
- **`PauseMenu._paused` is a static bool.** Mod UIs checking "is the game paused" should `PauseMenu._paused` (private field — reflect) or watch `Time.timeScale == 0`. There's no public event for the pause toggle.

---

## Mod-author "patch vs replace" matrix

| Surface | Patch in place? | Replace wholesale? | Notes |
|---|---|---|---|
| Vanilla Window chrome | ✗ Risky — `UpdateForShown` zaps children | ✓ Use your own Window prefab + `WindowManager` parent | Reuse `Window` prefab via `Object.Instantiate(WindowManager.Shared.windowPrefab)` if findable; else build your own |
| CarInspector — add a tab | ✓ Postfix `PopulatePanel` | — | Idempotent issue noted; safe pattern |
| CarInspector — add a row to existing tab | ✓ Postfix `PopulateCarPanel` etc. | — | Trivial; row appears under vanilla rows |
| CarInspector — replace entirely | ✗ Don't try to mutate vanilla layout | ✓ Patch `Show(Car)` prefix to call your own panel | The user's experiment confirmed this is the cleanest approach |
| HUD (`LocomotiveControlsUIAdapter`) — small additions | ✗ Brittle — re-parenting issues | ✓ Sibling panel anchored next to vanilla | Read selected loco from `TrainController.Shared.SelectedLocomotive` |
| HUD — replace | — | ✓ Disable `LocomotiveControlsUIAdapter` GameObject; build your own | Pattern proven by user's experiment |
| Preferences — add settings | ✓ Postfix `PreferencesBuilder.BuildTabFeatures` (or `BuildTabGraphics` etc.) | — | Easiest extension point |
| Preferences — add a tab | ✓ Postfix `PreferencesBuilder.Build` (must wrap the tabBuilder closure) | — | Use `_selectedTabState` in the static |
| ConsistInspectorPanel | ✗ Stub; not worth patching | ✓ Build your own; don't reference vanilla's | The user's experiment cloned this by mistake |
| Toast / ModalAlert | ✓ Patch `Toast.Present` / `ModalAlertController._Run` | ✓ Don't bother replacing — the API is small | Use as-is |
| MapWindow | ✓ Patch `MapBuilder.Rebuild` (see map-mods survey) | — | Map content is ScriptableObject driven |
| Console | — | ✓ Define commands via `[ConsoleCommand]` | Console is plugin-extensible by design |
| TopRightArea (clock + buttons) | ✓ Append a button via SerializeField hack | ✓ Build your own top-right cluster | Static sibling-panel pattern works |
| CompassHUD callouts | ✓ Use `AddLocationIndicator(token, descriptor)` | — | Public API; designed for extension |

---

## Cross-references

- **Wear / condition rendering** — `BuilderExtensions.AddConditionField`, `AddMileageField`, `AddRepairDestination` semantics: see [Wear & Durability › patch candidates](wear-durability.md#patch-candidates) and the [Toggle bypasses](wear-durability.md#toggle-bypasses-high-value-findings) section that explains why "Overhaul" repair option vanishes when `WearFeature` is off.
- **Brake/coupler state on the Car panel** — Brake Line, Cylinder, Hand Brake, Bleed are reading `Car.air.*`; Cut Out / MU live on `Car.ControlProperties`. See [Couplers › state writes](couplers.md#state-writes-applyendgearchange-is-the-only-door) for the analogous KVO pattern.
- **Coupler-related rows** — vanilla CarInspector does *not* surface coupler state; the user's pill-strip for coupler-forces was an experiment-side addition that's not in vanilla. Anglecock / cut-lever interactions live in the world (pickables), not in CarInspector. See [Couplers › pickable surface](couplers.md#pickable--interaction-surface).
- **Selected-car flow that drives both HUD and inspector** — `TrainController.Shared.SelectedCar` setter fires `Messenger<SelectedCarChanged>` (sent from `TrainController.cs`); `LocomotiveControlsUIAdapter.UpdateForSelectedCar` reads `TrainController.Shared.SelectedLocomotive`. See [Consist Integration](consist-integration.md) for `TrainController` ownership semantics.
