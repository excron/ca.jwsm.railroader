# Car Definitions & Components — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Definition/`, `Railroader-ILSPY/Assembly-CSharp/Model.ComponentBuilders/`, `Railroader-ILSPY/Assembly-CSharp/AssetPack.Runtime/`)
**Companions:** [Cars, Cargo & Loading](cars-cargo.md), [map-mods-vanilla-survey.md](../map-mods-vanilla-survey.md)

Railroader's content is **data-driven via JSON-serialized `ObjectDefinition`s packaged into per-pack `AssetPack`s** alongside a Unity AssetBundle of meshes/materials/animations/audio. A car is *defined* by a `CarDefinition` (`Kind = "Car"`) — or a subclass like `SteamLocomotiveDefinition` — that lists `LoadSlots`, references a model prefab via `ModelIdentifier`, and contains a `List<Component>` of typed component descriptors (`HeadlightComponent`, `LoadModelComponent`, `WhistleComponent`, etc). At runtime, `Car.Setup` instantiates the model GameObject and walks the components, dispatching each to its registered `IComponentBuilder` (auto-discovered via `[ComponentBuilder]` attribute scanning) which AddComponents the matching MonoBehaviour. Two component lifetimes (`Static`/`Model`) split components into "pre-model-load" vs "post-model-load" passes. Modders ship new content as new asset packs in `persistentDataPath/AssetPacks/`.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Model.Definition.ObjectDefinition` (abstract) | `Definition/Model.Definition.Data/ObjectDefinition.cs` | Root of the definition hierarchy. Carries `List<Component>`, abstract `Kind` |
| `Model.Definition.Data.CarDefinition` | `Definition/Model.Definition.Data/CarDefinition.cs` | Per-car data: archetype, model id, weight, slots, components |
| `Model.Definition.Component` (abstract) | `Definition/Model.Definition/Component.cs` | Base for typed component descriptors. `JsonSubtypes` switches on `Kind` |
| `Model.Definition.Container` | `Definition/Model.Definition/Container.cs` | The pack manifest — `List<ContainerItem>` |
| `Model.Definition.ContainerItem` | `Definition/Model.Definition/ContainerItem.cs` | `(Identifier, Metadata, ObjectDefinition)` triplet |
| `Model.Definition.ContainerSerialization` | `Definition/Model.Definition/ContainerSerialization.cs` | JSON read/write with camelCase + Unity converters |
| `AssetPack.Runtime.AssetPackRuntimeStore` | `AssetPack.Runtime/AssetPackRuntimeStore.cs` | Loads `Definitions.json` + `Bundle` from disk; refcounts asset loads |
| `Model.Database.PrefabStore` | `Assembly-CSharp/Model.Database/PrefabStore.cs` | Multi-store registry; per-identifier dispatch; static `Create()` discovers packs |
| `Model.Database.DefinitionChecker` | `Assembly-CSharp/Model.Database/DefinitionChecker.cs` | Validates car definitions on load (logs errors/warnings) |
| `Model.ComponentBuilders.*` (`[ComponentBuilder]`) | `Assembly-CSharp/Model.ComponentBuilders/` | `IComponentBuilder` impls — one per component type |
| `Model.ComponentFactory.BuildComponent(Component, ctx)` | `Assembly-CSharp/Model/ComponentFactory.cs:34` | Dispatch to the right builder; lazy-init `_builders` dict via attribute scan |
| `Model.ComponentSetup.Setup(...)` | `Assembly-CSharp/Model/ComponentSetup.cs:21` | Per-component GameObject + Transform + dispatch |
| `Model.Car.SetupComponents(ctx, lifetime)` | `Assembly-CSharp/Model/Car.cs:1390` | Per-car loop over `Definition.EnabledComponentsForLifetime` |
| `UI.CarEditor.CarEditorWindow` | `UI.CarEditor/CarEditorWindow.cs` | The vanilla in-game definition editor |

---

## Definition spine: from JSON to MonoBehaviour

```
On disk (per asset pack):
   <persistentDataPath or StreamingAssets>/AssetPacks/<packId>/
      ├── Bundle              ← Unity AssetBundle (meshes, mats, prefabs, anim clips, audio)
      ├── Catalog.json        ← AssetPack.Common.AssetPackCatalog: list of asset IDs in Bundle
      └── Definitions.json    ← serialized Model.Definition.Container

PrefabStore.Create()                                              ← Model.Database/PrefabStore.cs:56
   │  Scan Internal (StreamingAssets/AssetPacks/) → AddStore each
   │  Scan External (persistentDataPath/AssetPacks/) → AddStore each
   │  LoadAssetPackStatically("shared")                            ← preloads "shared" bundle
   │  CheckDefinitions()                                           ← runs DefinitionChecker
   ▼
Per-pack AssetPackRuntimeStore                                     ← AssetPack.Runtime/AssetPackRuntimeStore.cs
   │  Catalog()       → JSON deserialize Catalog.json
   │  Container()     → ContainerSerialization.Deserialize(Definitions.json) → calls Container.Awake()
   │  LoadAsset<T>(id, ct) → AssetBundle.LoadAssetAsync (refcounted)
   ▼
TrainController.PrefabStore (lazy-init at TrainController.cs:200)
   │  CarDefinitionInfoForIdentifier(id) → TypedContainerItem<CarDefinition>
   │  AssetPackIdentifierContainingDefinition(id) → packId
   ▼
Car.LoadModelsAsync                                                ← Car.cs:1160
   │  prefabStore.LoadAssetAsync<GameObject>(packId, Definition.ModelIdentifier)
   │  prefabStore.TruckPrefabForId(Definition.TruckIdentifier)
   ▼
Car.HandleModelsLoaded → DidLoadModels → SetupComponents(ctx, lifetime)
                                                                   ← Car.cs:1390
   │  foreach component in Definition.EnabledComponentsForLifetime(lifetime):
   │     ComponentSetup.Setup(objectName, component, ctx, parent, observeProperty, prefabInstantiator)
   │        new GameObject(component.Name)  // hidden, parented under car body
   │        ComponentBuilderContext ctx = new(...)
   │        ComponentFactory.BuildComponent(component, ctx)       ← Model/ComponentFactory.cs:34
   │           PrepareBuildersIfNeeded()   ← scans assembly for [ComponentBuilder]
   │           _builders[type].Build(ctx, component)
   │              → e.g. HeadlightComponentBuilder
   │                 ctx.InstantiatePrefab<HeadlightController>("headlight", ctx.GameObject.transform)
   │                 // sets headlightController fields from component fields
```

Two passes per car:
1. **`ComponentLifetime.Static`** — runs in `Car.Setup` (`Car.cs:1030`), parented to the Car GameObject. Used for components that don't need the loaded model (e.g., `LoadTargetComponent` with `Static` lifetime).
2. **`ComponentLifetime.Model`** — runs in `Car.DidLoadModels` (`Car.cs:1294`), parented under the body model after it's loaded. Most components are this.

`Definition.EnabledComponentsForLifetime(lifetime)` (`ObjectDefinition.cs:37`) filters by `Component.Enabled` AND by `[Component(..., Lifetime=…)]` attribute. **The attribute on the C# class governs lifetime, not a JSON property.**

---

## `Model.Definition.ObjectDefinition` — the root abstraction

```csharp
[JsonConverter(typeof(JsonSubtypes), new object[] { "Kind" })]
[JsonSubtypes.KnownSubType(typeof(CarDefinition),              "Car")]
[JsonSubtypes.KnownSubType(typeof(SteamLocomotiveDefinition),  "SteamLocomotive")]
[JsonSubtypes.KnownSubType(typeof(DieselLocomotiveDefinition), "DieselLocomotive")]
[JsonSubtypes.KnownSubType(typeof(MaterialDefinition),         "Material")]
[JsonSubtypes.KnownSubType(typeof(SceneryDefinition),          "Scenery")]
[JsonSubtypes.KnownSubType(typeof(TextureDefinition),          "Texture")]
[JsonSubtypes.KnownSubType(typeof(TruckDefinition),            "Truck")]
[JsonSubtypes.KnownSubType(typeof(WhistleDefinition),          "Whistle")]
public abstract class ObjectDefinition
{
    public List<Component> Components;
    public abstract string Kind { get; }
    public abstract void Awake();
    public TypedContainerItem<TDefinition> TypedContainerItem<TDefinition>(ContainerItem ci) { ... }
    public IEnumerable<Component> EnabledComponentsForLifetime(ComponentLifetime lifetime) { ... }
}
```

The `JsonSubtypes` attribute drives polymorphic serialization on the `Kind` discriminator. **Adding a new `ObjectDefinition` subclass requires registering it as a `KnownSubType`** — JSON deserialization will throw on unknown `Kind` strings. This is a hard gate for mod-defined definition types; you'd need to patch the JSON converter or fork.

`Awake()` is an abstract no-op for `CarDefinition` (`CarDefinition.cs:44`), called by `Container.Awake()` after deserialization. Used by some subclasses for post-load fixups.

### Known `ObjectDefinition` subclasses

| Class | Kind | Purpose |
|---|---|---|
| `CarDefinition` | "Car" | Boxcars, hoppers, cabooses — the base car descriptor |
| `SteamLocomotiveDefinition` | "SteamLocomotive" | + Wheelsets, drivers, boiler params, tender id |
| `DieselLocomotiveDefinition` | "DieselLocomotive" | + StartingTractiveEffort |
| `TruckDefinition` | "Truck" | Wheelset prefab + brake animation + wheel transforms |
| `SceneryDefinition` | "Scenery" | Map-placed props (silos, buildings) |
| `MaterialDefinition` | "Material" | Material override metadata |
| `TextureDefinition` | "Texture" | Texture metadata |
| `WhistleDefinition` | "Whistle" | Whistle audio + envelope |

---

## `CarDefinition` (the per-car prototype)

```csharp
public class CarDefinition : ObjectDefinition                     // Definition/Model.Definition.Data/CarDefinition.cs
{
    public override string Kind { get; } = "Car";
    public string ModelIdentifier { get; set; }                    // → AssetBundle GameObject id
    public string CarType { get; set; }                            // AAR car-type ("XM", "FB", "HM", ...)
    [JsonConverter(typeof(StringEnumConverter))]
    public CarArchetype Archetype { get; set; }
    public bool VisibleInPlacer { get; set; } = true;
    public int BasePrice { get; set; }
    public string BaseRoadNumber { get; set; } = "100000";
    public int WeightEmpty { get; set; }                           // pounds
    public string TruckIdentifier { get; set; }                    // → TruckDefinition id
    public List<LoadSlot> LoadSlots { get; set; } = new();
    public float TruckSeparation { get; set; }                     // meters between truck centers
    public virtual float Length { get; set; }                      // meters
    public float CouplerHeight { get; set; } = 0.8f;
    public Vector3 AirHosePosition { get; set; } = new(-0.345f, 0.877f, 0.1f);
    public List<AnimationReference> BrakeAnimations { get; set; }  // played by BrakeAnimator
    [JsonConverter(typeof(StringEnumConverter))]
    public CurveRadius MinimumCurveRadius { get; set; }            // {ExtraSmall, Small, Medium, Large, ExtraLarge}
}
```

`SteamLocomotiveDefinition` extends:

```csharp
public int PublishedTractiveEffort;
public float PositionHead, PositionTail;       // overrides Length to PositionHead - PositionTail
public int MainDriverIndex;
public List<Wheelset> Wheelsets;               // (Offset, Length, Diameter, NumberOfAxles, Animation, Transform)
public string TenderIdentifier;                // null = no tender (pulls coal+water in own slots)
public float PistonDiameterInches, PistonStrokeInches;
public float MaximumBoilerPressure;
public float TotalHeatingSurface = 3000f;
public float WeightOnDrivers;
```

`DieselLocomotiveDefinition` adds only `StartingTractiveEffort = 49500`.

### Key derived behaviors

`MinimumCurveRadius` → `Car.MaximumTrackCurvature` via `Car.CalculateMaximumTrackCurvature` (`Car.cs:2925`):

| `CurveRadius` | Maximum track curvature (degrees) |
|---|---|
| `ExtraSmall` | 40 |
| `Small` | 36 |
| `Medium` | 23 |
| `Large` | 17 |
| `ExtraLarge` | 14 |
| (default) | 1000 |

This drives `Car.ApplyCurvatureToModel` derail/damage thresholds — see [Wear › toggle bypasses](wear-durability.md#toggle-bypasses-high-value-findings).

### `LoadSlot`

```csharp
public class LoadSlot                                              // Definition/Model.Definition.Data/LoadSlot.cs
{
    public float MaximumCapacity { get; set; }
    [JsonConverter(typeof(StringEnumConverter))]
    public LoadUnits LoadUnits { get; set; }                       // Pounds | Gallons | Quantity
    public string RequiredLoadIdentifier { get; set; }             // null/empty = any load matches
}
```

Default constructor: `LoadUnits.Pounds`, `MaximumCapacity = 50000`, `RequiredLoadIdentifier = null`.

`CarDefinitionExtensions.DisplayOrderLoadSlots` (`CarDefinitionExtensions.cs:19`) sorts so `coal` < `water` < (file order). This is the inspector display order.

`LoadIdentifier` (`Model.Definition.Data/LoadIdentifier.cs`) — string constants:

```csharp
public const string Coal       = "coal";
public const string Water      = "water";
public const string DieselFuel = "diesel-fuel";
public const string Passengers = "passengers";
```

These are Load.id values referenced as `RequiredLoadIdentifier`. Mod loads use their own ids.

### `LoadUnits`

```csharp
public enum LoadUnits { Pounds, Gallons, Quantity }
```

See [Cars-Cargo › Load.Pounds(quantity)](cars-cargo.md#loadpoundsquantity-weight-conversion) for the unit→weight conversion.

### Patch candidates (CarDefinition)

| Method | Why patch |
|---|---|
| `CarDefinition.Awake` (no-op default) | Inject post-load fixups — but careful: `Definition` is shared across all cars of that identifier |
| `Car.ValidateDefinition` (`Car.cs:1038`) | Mutates definition in place. Patch to override the floor clamps (`Length<1`, `WeightEmpty<100`) |
| `JsonSubtypes` registration on `ObjectDefinition` | Required to add new `Kind` strings — patch `Container` setup or fork |
| `CarDefinitionExtensions.DisplayOrderLoadSlots` | Customize slot display order |

---

## `Model.Definition.Component` (the typed descriptor)

```csharp
[JsonConverter(typeof(JsonSubtypes), new object[] { "Kind" })]
[JsonSubtypes.KnownSubType(typeof(AggregateLoadModelComponent), "AggregateLoadModel")]
[JsonSubtypes.KnownSubType(typeof(BellComponent),               "Bell")]
[JsonSubtypes.KnownSubType(typeof(ChuffComponent),              "Chuff")]
// ... 30+ entries ...
public abstract class Component
{
    [DefinitionProperty(Order = -1010, Editable = false)] public abstract string Kind { get; }
    [DefinitionProperty(Order = -1006)] public string Name { get; set; }
    [DefinitionProperty(Order = -1004)] public PositionRotationScale Transform { get; set; } = Zero;
    [DefinitionProperty(Order = -1002)] public TransformReference Parent { get; set; }
    [DefinitionProperty(Order = -1006)] public bool Enabled { get; set; } = true;
    public override string ToString() => GetType().Name + " \"" + Name + "\"";
}
```

Same `JsonSubtypes` polymorphism as `ObjectDefinition` but on the per-component `Kind`. The full known-subtype list is in `Component.cs` lines 9-39 — see "Known component types" below for the index.

`[DefinitionProperty(Order=…)]` controls field display order in the editor (`UI.CarEditor`). `Editable=false` makes a field read-only.

`Parent` is a `TransformReference` — a `string[] Path` that walks the body's transform hierarchy. Resolved by `BodyTransform.ResolveTransform(component.Parent, defaultReturnsReceiver: true)` in `Car.SetupComponent` (`Car.cs:1455`). Empty path → body root.

### `[Component(...)]` attribute (governs runtime behavior)

```csharp
[AttributeUsage(AttributeTargets.Class)]
public class ComponentAttribute : Attribute
{
    public ComponentDefinitionMask DefinitionMask { get; set; }    // Any | Car | Scenery
    public ComponentLifetime       Lifetime       { get; set; }    // Static | Model
    public bool IsCompatibleWith(ObjectDefinition definition);     // checks DefinitionMask
}
```

Each component type carries this attribute, e.g.:

```csharp
[Component(ComponentDefinitionMask.Car, ComponentLifetime.Model)]
public class HeadlightComponent : Component { ... }

[Component(ComponentDefinitionMask.Car, ComponentLifetime.Static)]
public class LoadTargetComponent : Component { ... }
```

`Car.SetupComponents` reads via `Definition.EnabledComponentsForLifetime(lifetime)` which checks the attribute.

`HideInEditorAttribute` (`Definition/Model.Definition/HideInEditorAttribute.cs`) hides a type from the `Add Component` dropdown in `CarEditorWindow.ConfigureAddComponentDropdown` (`CarEditorWindow.cs:265`).

### Known component types (`ComponentKind` strings)

From `Definition/Model.Definition/ComponentKind.cs` and `Component.cs` registrations:

| Kind | Class | Lifetime | Mask | Builder |
|---|---|---|---|---|
| `AggregateLoadModel` | `AggregateLoadModelComponent` | Model | Car | `AggregateLoadModelComponentBuilder` |
| `Bell` | `BellComponent` | Model | Car | `BellComponentBuilder` |
| `Chuff` | `ChuffComponent` | Model | Car | `ChuffComponentBuilder` |
| `ClassLight` | `ClassLightComponent` | Model | Car | `ClassLightComponentBuilder` |
| `Colorizer` | `ColorizerComponent` | Model | Car | `ColorizerComponentBuilder` |
| `Compressor` | `CompressorComponent` | Model | Car | `CompressorComponentBuilder` |
| `CylinderCock` | `CylinderCockComponent` | Model | Car | `CylinderCockComponentBuilder` |
| `Decal` | `DecalComponent` | Model | Car | `DecalComponentBuilder` |
| `DerailedEffect` | `DerailedEffectComponent` | (model, runtime-injected) | Car | `DerailedEffectComponentBuilder` |
| `DetailModel` | `DetailModelComponent` | Model | Car | `DetailModelComponentBuilder` |
| `DieselExhaust` | `DieselExhaustComponent` | Model | Car | `DieselExhaustComponentBuilder` |
| `Dynamo` | `DynamoComponent` | Model | Car | `DynamoComponentBuilder` |
| `FireboxEffect` | `FireboxEffectComponent` | Model | Car | `FireboxEffectComponentBuilder` |
| `Gauge` | `GaugeComponent` | Model | Car | `GaugeComponentBuilder` |
| `Headlight` | `HeadlightComponent` | Model | Car | `HeadlightComponentBuilder` |
| `Horn` | `HornComponent` | Model | Car | `HornComponentBuilder` |
| `Ladder` | `LadderComponent` | Model | Car | `LadderComponentBuilder` |
| `LightFixture` | `LightFixtureComponent` | Model | Car | `LightFixtureComponentBuilder` |
| `LoadAnimation` | `LoadAnimationComponent` | Model | Car | `LoadAnimationComponentBuilder` |
| `LoadModel` | `LoadModelComponent` | Model | Car | `LoadModelComponentBuilder` |
| `LoadTarget` | `LoadTargetComponent` | **Static** | Car | `LoadTargetComponentBuilder` |
| `MapMask` | `LegacyMapMaskComponent` | Model? | Any | `LegacyMapMaskComponentBuilder` |
| `RectangleMapMask` | `RectangleMapMaskComponent` | Model? | Any | `MapMaskComponentBuilder` |
| `CircleMapMask` | `CircleMapMaskComponent` | Model? | Any | `MapMaskComponentBuilder` |
| `MarkerLight` | `MarkerLightComponent` | Model | Car | `MarkerLightComponentBuilder` |
| `PrefabControl` | `PrefabControlComponent` | Model | Car | `PrefabControlComponentBuilder` |
| `RadialControl` | `RadialControlComponent` | Model | Car | `RadialControlComponentBuilder` |
| `Seat` | `SeatComponent` | Model | Car | `SeatComponentBuilder` |
| `ToggleAnimation` | `ToggleAnimationComponent` | Model | Car | `ToggleAnimationComponentBuilder` |
| `ToggleControl` | `ToggleControlComponent` | Model | Car | `ToggleControlComponentBuilder` |
| `Whistle` | `WhistleComponent` | Model | Car | `WhistleComponentBuilder` |

**`DerailedEffect` is special** — `Car.SetupComponents` injects it explicitly (`Car.cs:1411-1417`) for every Model lifetime pass, regardless of whether it's listed in `Definition.Components`. The component class doesn't appear to be a `[ComponentBuilder]`-attributed subtype in `KnownSubType` registrations either; it's added as an inline instance.

---

## `IComponentBuilder` and the builder factory

```csharp
public interface IComponentBuilder                                 // Model/IComponentBuilder.cs
{
    Type ComponentType { get; }
    void Build(ComponentBuilderContext ctx, Component component);
}

[AttributeUsage(AttributeTargets.Class)]
public class ComponentBuilderAttribute : Attribute { }             // Model.ComponentBuilders/ComponentBuilderAttribute.cs
```

`ComponentFactory.PrepareBuildersIfNeeded` (`Model/ComponentFactory.cs:13`) lazily scans the **assembly of `ComponentFactory` itself** for `[ComponentBuilder]`-attributed types, instantiates each via `Activator.CreateInstance`, and registers in `_builders[builder.ComponentType] = builder`.

```csharp
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
```

**One-level base-class fallback.** A subtype without its own builder will reuse the parent's. Beyond one level, you get a warning and the component is silently dropped.

### `ComponentBuilderContext` (the builder's API surface)

`Model/ComponentBuilderContext.cs:12`:

```csharp
public readonly struct ComponentBuilderContext : IDefinitionReferenceResolver, IPrefabInstantiator
{
    public GameObject GameObject { get; }                  // the per-component sub-GO
    public GameObject AnimatorGameObject { get; }          // body's AnimationMap.gameObject
    public CarColorController CarColorController { get; }
    public string ObjectName { get; }                      // for diagnostics

    public T InstantiatePrefab<T>(string name, Transform parent) where T : Component;
    public Transform Resolve(TransformReference);          // walks GameObject.transform.parent (body root)
    public AnimationClip Resolve(AnimationReference);      // via _animationMap
    public bool TryResolve(AnimationReference, out AnimationClip);
    public Material Resolve(MaterialReference);            // via _materialMap (may log error if null)
    public void ObserveProperty(string key, Action<Value>);              // KVO observer registration
    public void ObserveProperty(PropertyChange.Control, Action<Value>);  // typed wrapper
}
```

The `_observeProperty` delegate is closed over by `Car.SetupComponent` (`Car.cs:1458`) and routes to either `Observers` (Static) or `_controlObservers` (Model) — **the latter is disposed on `UnloadModels`**, so model-lifetime KVO observers don't leak when models cull-unload.

### `ComponentSetup.Setup` (the per-component bootstrap)

```csharp
public static void Setup(string objectName, Component component, Context setupContext, Transform parent,
                         Action<string, Action<Value>> observeProperty, IPrefabInstantiator prefabInstantiator)
{
    GameObject go = new GameObject(component.Name);
    go.hideFlags = HideFlags.DontSave;
    go.SetActive(value: false);                // built while inactive
    Transform t = go.transform;
    t.SetParent(parent, worldPositionStays: false);
    t.localPosition = component.Transform.Position;
    t.localRotation = component.Transform.Rotation;
    t.localScale    = component.Transform.Scale;
    var ctx = new ComponentBuilderContext(...);
    ComponentFactory.BuildComponent(component, ctx);
    go.SetActive(value: true);                 // activated after Build returns
}
```

**Builder code runs while the component GameObject is inactive.** This means `OnEnable` of any added MonoBehaviour will fire AFTER `Build` returns, when the GameObject becomes active. If you patch a builder, do post-init in `OnEnable` or via a coroutine, not in the build call directly.

---

## Example builders (templates for mod-side implementation)

### Trivial: `HeadlightComponentBuilder`

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
        hc.Direction = component.Forward ? Forward : Reverse;
    }
}
```

`InstantiatePrefab` reaches into `TrainController.Shared.PrefabInstantiator` — a registry of named prefabs (headlight, etc.) that the game ships and cars reuse.

### KVO-driven: `ToggleAnimationComponentBuilder`

```csharp
private void _Build(ComponentBuilderContext ctx, ToggleAnimationComponent component)
{
    var picker = ctx.Resolve(component.TargetColliderObject).gameObject.AddComponent<KeyValuePickableToggle>();
    picker.key = component.Key;
    picker.displayTitle = component.Title;
    // ...
    if (!ctx.TryResolve(component.Animation, out AnimationClip clip))
        Log.Error("Couldn't resolve animation: {anim}", component.Animation);
    var anim = ctx.AnimatorGameObject.AddComponent<KeyValueBoolAnimator>();
    anim.animationClip = clip;
    anim.key = component.Key;
    anim.speed = component.Speed;
}
```

`KeyValuePickableToggle` and `KeyValueBoolAnimator` are MonoBehaviours that subscribe to the same `Key` — clicking the picker writes the bool, the animator plays/reverses the clip on change. **No explicit observer is registered via `ctx.ObserveProperty` here** — the MonoBehaviours self-subscribe in `OnEnable` via `GetComponentInParent<KeyValueObject>()`.

### `LoadModelComponentBuilder` → `CarLoadModelController`

```csharp
private void _Build(ComponentBuilderContext ctx, LoadModelComponent component)
{
    ctx.GameObject.AddComponent<CarLoadModelController>()
       .Configure(component.SlotIndex, component.LoadIdentifier, component.Models, component.Instances);
}
```

`CarLoadModelController.Configure` (`RollingStock/CarLoadModelController.cs:60`) async-loads each `AssetReference` in `component.Models` via `prefabStore.LoadAssetAsync<GameObject>`. `LoadIdentifier` can be `"*"` (matches any load) or a specific id. `Instances` is a list of `PositionRotationScale` for where to place each load model instance; the controller instantiates from the `Models` list cyclically (`index % models.Count`).

### `LoadTargetComponent`

```csharp
[Component(ComponentDefinitionMask.Car, ComponentLifetime.Static)]
public class LoadTargetComponent : Component
{
    public override string Kind { get; } = "LoadTarget";
    public float Radius { get; set; }
    public int SlotIndex { get; set; }
}
```

Builder adds a `CarLoadTarget` MonoBehaviour with `slotIndex` and `radius` (clamped ≥ 0.3). This is the spatial marker that `CarLoadTargetLoader` (industry-side, see [Cars-Cargo › Player-driven loading](cars-cargo.md#player-driven-loading-carloadtargetloader)) finds via Unity transforms when a car parks at a loader.

---

## Asset references

```csharp
public class AssetReference                                        // Model.Definition.Data/AssetReference.cs
{
    public string AssetPackIdentifier { get; set; }                // null = same pack as the containing definition
    public string AssetIdentifier { get; set; }
    [JsonIgnore][DefinitionProperty(Hidden = true)]
    public bool IsEmpty => string.IsNullOrEmpty(AssetIdentifier);
}

public class AbsoluteAssetReference                                // resolved version
{
    public string AssetPackIdentifier;                             // never null
    public string AssetIdentifier;
}
```

`PrefabStore.ResolveAssetReference(contextualDefinitionId, assetReference)` (`PrefabStore.cs:292`) fills in the pack identifier from the contextual definition's pack when `AssetPackIdentifier` is null.

`AnimationReference(string ClipName)` and `MaterialReference(string MaterialName)` are name-based — resolved by `AnimationMap` / `MaterialMap` MonoBehaviours on the loaded body model. The AssetBundle ships the body prefab WITH an `AnimationMap` listing all clip names→AnimationClip refs, and a `MaterialMap` listing all material names→Material refs. Definitions reference by name; the maps do the lookup at load time.

`TransformReference(string[] Path)` walks the body transform tree by exact name match per path element. `TransformReferenceExtensions.ResolveTransform` (used in `Car.cs:1455`) takes `defaultReturnsReceiver: true` to return the receiver when path is null/empty.

---

## On-disk asset pack layout

`AssetPackRuntimeStore` (`AssetPack.Runtime/AssetPackRuntimeStore.cs`):

```csharp
private string BasePath        => Path.Combine(BasePathForLocation(Location), Identifier);
private string AssetBundlePath => Path.Combine(BasePath, "Bundle");
private string CatalogPath     => Path.Combine(BasePath, "Catalog.json");
private string DefinitionsPath => Path.Combine(BasePath, "Definitions.json");

public static string BasePathForLocation(StoreLocation location) => location switch
{
    StoreLocation.Internal => Path.Combine(Application.streamingAssetsPath, "AssetPacks"),
    StoreLocation.External => Path.Combine(Application.persistentDataPath, "AssetPacks"),
};
```

So per-pack:

```
<persistentDataPath>/AssetPacks/<packIdentifier>/
   ├── Bundle              ← Unity AssetBundle (binary)
   ├── Catalog.json        ← AssetPackCatalog: { assets: { id → metadata } }
   └── Definitions.json    ← Container: { Objects: [ ContainerItem, ... ] }
```

`PrefabStore.Create()` discovers packs via `Utilities.FindAssetPacks(BasePathForLocation(location))` for both Internal (StreamingAssets) and External (persistentDataPath).

**External wins on identifier collision** (`PrefabStore.cs:86-108` `AddStore`): "Replace Store" log message; the existing store is `Dispose`d and replaced. Ordering: Internal stores added first, then External — so External clobbers Internal. **This is the mod-override mechanism.**

`PrefabStore.LoadAssetPackStatically("shared")` keeps the `shared` AssetBundle resident at game launch. Other bundles are loaded lazily on first asset request and unloaded when refcount hits zero (`AssetPackRuntimeStore.UnloadAssetBundleWithNoRemainingReferences`).

---

## `Container` and serialization

```csharp
public class Container                                             // Definition/Model.Definition/Container.cs
{
    public List<ContainerItem> Objects { get; set; } = new();
    public void Awake() { foreach (var o in Objects) o.Awake(); }   // calls Definition.Awake() per item
}

public class ContainerItem                                         // ContainerItem.cs
{
    [JsonProperty(Order = -200)] public string Identifier { get; set; }
    [JsonProperty(Order = -100)] public ObjectMetadata Metadata { get; set; }
    public ObjectDefinition Definition { get; set; }               // polymorphic via JsonSubtypes
    public void Awake() => Definition?.Awake();
}

public class ObjectMetadata                                        // ObjectMetadata.cs
{
    public string Name;
    public string Description;
    public List<string> Tags = new();
    public string Credits;
}
```

`ContainerSerialization.Deserialize(text)` (`ContainerSerialization.cs:9`):
- Newtonsoft.Json with `CamelCasePropertyNamesContractResolver` and `CamelCaseNamingStrategy(ProcessDictionaryKeys=false, OverrideSpecifiedNames=true)`.
- Custom converters: `Vec2Conv`, `Vec3Conv`, `QuaternionConv`.
- Calls `container.Awake()` after deserialize (which fires `ObjectDefinition.Awake` per item).

`Serialize(container)` writes Indented format (human-readable JSON).

`CloneViaSerialization(Component)` round-trips a single component through JSON for cloning — used by the editor's `Duplicate` button.

---

## `PrefabStore` (the runtime registry)

`Model.Database.PrefabStore : IPrefabStore, IDisposable` (`Model.Database/PrefabStore.cs:16`).

```csharp
public interface IPrefabStore                                      // Model.Database/IPrefabStore.cs
{
    IEnumerable<TypedContainerItem<CarDefinition>> AllCarDefinitionInfos { get; }
    Task<Wheelset> TruckPrefabForId(string truckIdentifier);
    T DefinitionForIdentifier<T>(string definitionIdentifier, out ObjectMetadata metadata);
    IEnumerable<TypedContainerItem<TDefinition>> AllDefinitionInfosOfType<TDefinition>() where TDefinition : ObjectDefinition;
    TypedContainerItem<CarDefinition> CarDefinitionInfoForIdentifier(string identifier);
    Task<LoadedAssetReference<T>> LoadAssetAsync<T>(string assetPackIdentifier, string assetIdentifier, CancellationToken ct) where T : Object;
    string AssetPackIdentifierContainingDefinition(string definitionIdentifier);
    AbsoluteAssetReference ResolveAssetReference(string contextualDefinitionIdentifier, AssetReference assetReference);
}
```

Single accessor: `TrainController.PrefabStore` (lazy-init at `TrainController.cs:200`). The cast to concrete `PrefabStore` exposes `ExternalStores` (used by the in-game definition editor to enumerate mod packs).

`AssetPackContainingIdentifier(identifier)` walks `_stores` and returns the FIRST containing the id, throwing `UnknownIdentifierException` if none has it.

`TruckPrefabForId` is a memoized async — produces a `Wheelset` MonoBehaviour wrapped around the loaded truck prefab, configured with `TruckDefinition.WheelTransforms`, `BrakeAnimation`, `Diameter`. **The truck reference is held permanently** in `_truckReferences` until `PrefabStore.Dispose` — trucks never unload mid-session.

`PrefabStoreExtensions.Random(carTypeFilter, sizePreference, rnd)` (`PrefabStoreExtensions.cs:22`) is the random-car-pick used by industry car ordering (`OrderEmpty` etc).

### Patch candidates (PrefabStore)

| Method | Why patch |
|---|---|
| `PrefabStore.Create` (static) | Add additional store roots beyond StreamingAssets/persistentDataPath. Patch via prefix to mutate the `_stores` list before `CheckDefinitions` runs |
| `PrefabStore.AddStore` | Inject metadata; intercept identifier collisions |
| `PrefabStore.AssetPackContainingIdentifier` | Fallback resolution for missing identifiers (route to a default pack or generate on the fly) |
| `PrefabStore.AllCarDefinitionInfos` | Add synthetic car definitions not backed by an asset pack — but careful, `AssetPackIdentifierContainingDefinition(id)` will throw later in `Car.LoadModelsAsync` |
| `IPrefabStore` impl | Replace entirely — but `_prefabStore = Model.Database.PrefabStore.Create()` is wired directly in `TrainController.PrefabStore` getter |

---

## `DefinitionChecker` (load-time validation)

`Model.Database.DefinitionChecker` (`Assembly-CSharp/Model.Database/DefinitionChecker.cs`) runs in `PrefabStore.Create` → `CheckDefinitions`:

- For freight cars: `LoadSlots` non-empty; `BasePrice == 0` unless `CarType ∈ {FB, FL, HM, HT, TM, XM}`.
- For tenders: `BasePrice == 0`; exactly 2 LoadSlots with `coal` + `water`.
- `ModelIdentifier` not empty AND present in the pack's `Catalog.assets`.
- LoadSlot units must match required identifier: coal=Pounds, water=Gallons, logs=Quantity. `MaximumCapacity > 0.1`.
- For steam locomotives: `BasePrice > 0`; either `TenderIdentifier` present in same pack OR `LoadSlots.Count == 2` with coal+water (and LoadTargetComponent for each).

Errors and warnings are accumulated, then `PrintToLog()` emits via Serilog. **A pack that throws during `Check` is removed from `_stores` entirely** — so a malformed pack silently disappears without preventing game start.

---

## `UI.CarEditor.CarEditorWindow` (the in-game definition editor)

`UI.CarEditor/CarEditorWindow.cs` (388 lines). Runtime Level Designer (RLD) integration. Activated via `DefinitionEditorModeController`.

### Workflow

1. `DefinitionEditorModeController.OnGUI` shows pack list (External stores only).
2. Click a pack → `DrawGUIStore` lists its `Container().Objects`. Each is clickable.
3. Click a definition → `EditItem(item)`:
   - For `CarDefinition`: `EditItemCar` — calls `TrainController.PlaceTrain(starter-editor-marker, [this car descriptor])`. Spawns the car for live preview.
   - For `SceneryDefinition`: instantiates a `SceneryAssetInstance` for editing.
   - Other definitions: opens editor without instantiation.
4. `CarEditorWindow.Show(store, identifier, getParentPositionRotation, onChanged)` opens the actual editor with primary (component list) + secondary (selected-component property panel) panes.
5. `Apply` → `_store.SaveContainer()` writes `Definitions.json` back to disk + reruns `DefinitionChecker`.

### Add Component dropdown

`ConfigureAddComponentDropdown` (`CarEditorWindow.cs:265`) reflects over the **`ComponentAttribute`'s containing assembly** (i.e., `Definition.dll`) for all `Component` subclasses, filters by `IsCompatibleWith(definition)`, and excludes `[HideInEditor]`.

So a mod that wants to add a custom component visible in the editor must:
1. Define the `Component` subclass in an assembly (likely a separate mod DLL).
2. Register it as a `JsonSubtypes.KnownSubType` on `Component`.
3. Patch `CarEditorWindow.ConfigureAddComponentDropdown` to widen the assembly scan to include the mod's assembly. (Currently scoped to `typeof(ComponentAttribute).Assembly`.)
4. Register an `[ComponentBuilder]`-attributed builder somewhere `ComponentFactory.PrepareBuildersIfNeeded` will scan — i.e., the main Assembly-CSharp.dll. (`ComponentFactory` scans **only** `typeof(ComponentFactory).Assembly` (`ComponentFactory.cs:20`), so a mod-supplied builder needs the factory to be re-prepared with a wider scan.)

`DefinitionEditorModeController.NewContainerItem` (`DefinitionEditorModeController.cs:195`) also offers `Steam Locomotive`, `Diesel`, `Car`, `Truck`, `Whistle`, `Scenery`, `Material` as new-definition creation buttons. Same constraint: hardcoded list.

### Patch candidates (Editor)

| Method | Why patch |
|---|---|
| `ComponentFactory.PrepareBuildersIfNeeded` | Widen the assembly scan to include mod assemblies (e.g., `AppDomain.CurrentDomain.GetAssemblies()`) |
| `CarEditorWindow.ConfigureAddComponentDropdown` | Same — widen Add Component dropdown to mod components |
| `DefinitionEditorModeController.DrawGUINewItem` | Add new-definition buttons for mod-defined `ObjectDefinition` subclasses |
| `Container.Awake` / `ContainerItem.Awake` | Inject post-load fixups across all loaded definitions |
| `ContainerSerialization.JsonSerializerSettings` (private static) | Add custom converters; can't patch private static — patch `Deserialize`/`Serialize` instead |

---

## `Load` ScriptableObject (the cargo catalog)

`Load` is **not** an `ObjectDefinition` — it's a Unity `ScriptableObject` (`[CreateAssetMenu]`) shipped in Unity assets, NOT in `Definitions.json`. See [Cars-Cargo › Load.Pounds(quantity)](cars-cargo.md#loadpoundsquantity-weight-conversion) for the data shape.

The catalog: `CarPrototypeLibrary.opsLoads` (a `Load[]`) is set as a serialized field on the `CarPrototypeLibrary` ScriptableObject, in turn referenced from `TrainController.carPrototypeLibrary` (serialized field). `TrainController.cs:382` sets `CarPrototypeLibrary.instance = carPrototypeLibrary`.

Adding a mod-defined `Load` is therefore a Unity-asset-side operation, NOT a JSON-definition-side one. The cleanest mod path:
1. Create the `Load` SO in a Unity authoring project, package into an AssetBundle.
2. At runtime, load the SO from your AssetBundle and append to `CarPrototypeLibrary.instance.opsLoads`.
3. Optionally patch `CarPrototypeLibrary.LoadForId` to fall back to a mod registry if not found.

Note: this couples cargo to Unity assets, while car definitions are JSON. **The asymmetry is deliberate** — `Load` carries gameplay parameters (cost, payment, density) that the game expects to query as serialized field defaults from a Unity inspector, not as JSON.

---

## End-to-end mod patterns

### Adding a new car definition (no new components)

1. Create an asset pack folder under `<persistentDataPath>/AssetPacks/<packId>/`.
2. Build a Unity AssetBundle containing your model GameObject + its child `AnimationMap`/`MaterialMap` MonoBehaviours. Place at `<packId>/Bundle`.
3. Write `<packId>/Catalog.json` listing your asset ids.
4. Write `<packId>/Definitions.json` containing a `Container` with one `ContainerItem` whose `Definition` is a `CarDefinition` referencing your model id and listing your `LoadSlots` and reused vanilla `Components` (e.g., a `Headlight` referencing one of your transforms).
5. `PrefabStore.Create` will discover the pack at startup. `DefinitionChecker` will validate.
6. The car will appear in the placer if `VisibleInPlacer = true` and `CarType` matches a filter.

No code changes required for vanilla components.

### Adding a new component type

1. Define the `[Component(...)]`-attributed `Component` subclass in a mod assembly.
2. **Patch `JsonSubtypes` registration** on `Component` to add your `KnownSubType` mapping — no public API for this; you must patch the static type initializer or use Newtonsoft.Json's `JsonSubtypes` registration API (which is reflective).
3. Define an `[ComponentBuilder]`-attributed `IComponentBuilder` for your component in your mod assembly.
4. **Patch `ComponentFactory.PrepareBuildersIfNeeded`** to scan your assembly (it currently scans only `typeof(ComponentFactory).Assembly`, which is `Assembly-CSharp.dll`).
5. (Optional, for editor support) Patch `CarEditorWindow.ConfigureAddComponentDropdown` to include your assembly.

The bones of `ComponentFactory.BuildComponent`'s base-class fallback (`type.BaseType` lookup) gives you a path to subclass an existing component without writing a builder — but only one level deep.

### Replacing a vanilla car

Drop a pack under `<persistentDataPath>/AssetPacks/<packId>/` whose `<packId>` matches a vanilla pack name. External wins on collision (`PrefabStore.AddStore` log: "Replace Store"). The vanilla pack is `Dispose`d and replaced wholesale — you can't selectively override one definition without re-shipping the rest.

To override a single definition without replacing the pack: write a small mod pack (different `<packId>`) with the same definition `Identifier` as the target. **Identifier collisions across packs throw at definition-resolve time** — `AssetPackContainingIdentifier` walks stores in addition order and returns the first match. You'd need to patch resolution order or `AssetPackContainingIdentifier` to win.

### Modifying a definition at runtime

`CarDefinition` instances are shared across all cars sharing the identifier. Mutating fields (e.g., `Definition.WeightEmpty = 80000`) affects every existing car AND every future car spawned from that definition. The current car's `_loadWeight` will only refresh on the next `load.{n}` write (UpdateLoadWeight observer fan-out). Force a refresh by writing-and-restoring an existing key, or call `car.UpdateLoadWeight()` directly via reflection.

`Car.ValidateDefinition` clamps min values on first car spawn — once clamped, subsequent inspects show the clamped value, not your override.

---

## Cross-references

- How `Car.Setup` and `Car.HandleModelsLoaded` consume these definitions — see [Cars-Cargo › Lifecycle spine](cars-cargo.md#lifecycle-spine).
- How `LoadSlot` interacts with cargo at runtime — see [Cars-Cargo › Cargo / loading](cars-cargo.md#cargo--loading).
- The `BrakeAnimations` field on `CarDefinition` feeds [Wear › TODO brakes crib sheet] and the vanilla `BrakeAnimator` MonoBehaviour.
- `MinimumCurveRadius` → `Car.MaximumTrackCurvature` → curve-overspeed damage path: see [Wear › toggle bypasses](wear-durability.md#toggle-bypasses-high-value-findings).
- The data-driven content pattern — `ObjectDefinition` + `Components` + asset packs — is shared with map scenery; see [map-mods-vanilla-survey.md](../map-mods-vanilla-survey.md).
- Definition editing also exists for scenery, materials, trucks, and whistles — same `CarEditorWindow` machinery, different `DefinitionEditor` subclass (`SteamLocomotiveDefinitionEditor` is the only specialized one in vanilla).
