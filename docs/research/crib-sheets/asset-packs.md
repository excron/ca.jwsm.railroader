# Asset Packs & Prefab Store — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/AssetPack.Common/`, `Railroader-ILSPY/Assembly-CSharp/AssetPack.Runtime/`, `Railroader-ILSPY/Assembly-CSharp/Model.Database/`)
**Companions:** [Car Definitions & Components](car-definitions.md), [Cars, Cargo & Loading](cars-cargo.md), [Audio](audio.md), [Save / Load](save-load.md), [Multiplayer Core](multiplayer-core.md), [map-mods-vanilla-survey.md](../map-mods-vanilla-survey.md)

Asset packs are Railroader's content-distribution unit. Each pack is a single on-disk directory containing exactly three files — a Unity AssetBundle (`Bundle`), a JSON catalog of asset ids (`Catalog.json`), and a JSON list of `ObjectDefinition`s (`Definitions.json`). At runtime two roots are scanned (`StreamingAssets/AssetPacks/` and `persistentDataPath/AssetPacks/`) and each subfolder containing a `Catalog.json` becomes one `AssetPackRuntimeStore`. The stores are aggregated into a single `PrefabStore` keyed by definition identifier — the **first** store containing a given identifier wins lookup. `external` location replaces `internal` location wholesale on a pack-identifier collision (`PrefabStore.AddStore` `Dispose`s and swaps), but cross-pack identifier collisions resolve by **insertion order**, not by location. The `shared` pack is preloaded statically at game launch and never unloads; every other pack is loaded on first asset request and unloaded once its refcount returns to zero. There is **no MP synchronization of asset packs** — each peer scans its own filesystem, the snapshot stream carries only string identifiers, and a missing definition on the receiving side is silently dropped (the car is removed from the world, the failure is logged once).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `AssetPack.Common.AssetPackCatalog` | `AssetPack.Common/AssetPackCatalog.cs` | The on-disk `Catalog.json` shape: `{identifier, name, shared, assets:Dictionary<string,Asset>}` |
| `AssetPack.Common.Asset` | `AssetPack.Common/Asset.cs` | Per-asset metadata: `{name, type, filename}` |
| `AssetPack.Common.Filenames` | `AssetPack.Common/Filenames.cs` | Hard constants: `"Bundle"`, `"Catalog.json"`, `"Definitions.json"` |
| `AssetPack.Common.Utilities.FindAssetPacks(basePath)` | `AssetPack.Common/Utilities.cs:12` | Discovers candidate packs; gates on `Catalog.json` existence |
| `AssetPack.Common.Utilities.ValidateIdentifier(str)` | `AssetPack.Common/Utilities.cs:24` | Slugifier: `[^a-z0-9-\.]` removed, whitespace collapsed |
| `AssetPack.Common.AnimationMap` / `MaterialMap` | `AssetPack.Common/{AnimationMap,MaterialMap}.cs` | Body-prefab MonoBehaviours mapping name→clip / name→material |
| `AssetPack.Runtime.AssetPackRuntimeStore` | `AssetPack.Runtime/AssetPackRuntimeStore.cs` | One per pack on disk; loads/unloads its `AssetBundle`, owns `Catalog`/`Container` parses |
| `AssetPack.Runtime.LoadedAssetReference<T>` | `AssetPack.Runtime/LoadedAssetReference.cs` | RAII handle: `Dispose()` decrements per-asset refcount in the owning store |
| `Model.Database.IPrefabStore` | `Model.Database/IPrefabStore.cs` | Public interface used by every consumer (Car, Whistle, Scenery, UI…) |
| `Model.Database.PrefabStore` | `Model.Database/PrefabStore.cs` | Aggregates `_stores`; `Create()` is the entire bootstrap; the only `IPrefabStore` impl |
| `Model.Database.PrefabStore.AddStore` | `PrefabStore.cs:86` | Same-id collision handler: `Dispose()` + replace; **logs only `External` adds** |
| `Model.Database.PrefabStore.UnknownIdentifierException` | `PrefabStore.cs:18` | Thrown by `AssetPackContainingIdentifier`; caught in two places only |
| `Model.Database.DefinitionChecker` | `Model.Database/DefinitionChecker.cs` | Per-pack validator; emits warnings/errors but a pack is dropped only if `Check` itself **throws** |
| `TrainController.PrefabStore` getter | `TrainController.cs:200` | Lazy-init singleton: `PrefabStore.Create()` — runs the entire scan first time it's read |
| `Helpers.SceneryAssetManager` | `Helpers/SceneryAssetManager.cs` | Edit-mode parallel store (separate `PrefabStore`!) — does not share refcount with `TrainController.PrefabStore` |

---

## On-disk layout

```
<root>/AssetPacks/<packIdentifier>/
   ├── Bundle              ← Unity AssetBundle (binary, no extension)
   ├── Catalog.json        ← AssetPackCatalog: identifier, name, shared, assets dict
   └── Definitions.json    ← Container: {Objects:[ContainerItem,…]} (optional!)
```

**Roots:**

| Location enum | Path | When |
|---|---|---|
| `StoreLocation.Internal` | `Application.streamingAssetsPath/AssetPacks/` | Ship-with-game packs (vanilla `shared`, the stock car packs) |
| `StoreLocation.External` | `Application.persistentDataPath/AssetPacks/` | User-installed mods. On Windows: `%USERPROFILE%/AppData/LocalLow/Giraffe Lab/Railroader/AssetPacks/` |

`AssetPackRuntimeStore.BasePathForLocation(location)` (`AssetPackRuntimeStore.cs:54`) is the single arbiter — patch this method to redirect a location to a different filesystem root (e.g., loading mods from a shared folder).

```csharp
public static string BasePathForLocation(StoreLocation location) => location switch {
    StoreLocation.Internal => Path.Combine(Application.streamingAssetsPath, "AssetPacks"),
    StoreLocation.External => Path.Combine(Application.persistentDataPath, "AssetPacks"),
    _ => throw new ArgumentOutOfRangeException(),
};
```

**Pack identifier == directory name.** `Utilities.FindAssetPacks` (`Utilities.cs:12`) returns `Path.GetFileName(dirPath)` for each subdir of the root that contains `Catalog.json`. There's no in-pack manifest field that overrides this — the directory name *is* the pack identifier used everywhere. (`Catalog.identifier` exists but is never consulted at runtime.)

**`Catalog.json` is required.** A directory without `Catalog.json` is silently skipped; no warning, no log. A pack with `Catalog.json` but missing `Bundle` will fail at first `LoadAsset` call (`AssetBundle.LoadFromFileAsync` returns null → `Exception("Failed to load asset bundle: …")`), but the store remains in `_stores` and may swallow further requests.

**`Definitions.json` is optional.** `AssetPackRuntimeStore.Container()` (`AssetPackRuntimeStore.cs:196`):

```csharp
if (!File.Exists(DefinitionsPath)) {
    _container = new Container();   // empty
    return _container;
}
```

A pure-asset pack (e.g., a "shared" pack of textures referenced by other packs' definitions) is legal and common.

---

## File formats

### `Catalog.json`

```jsonc
{
  "identifier": "my-pack",            // human label only; not used for resolution
  "name": "My Modded Boxcars",        // display name only
  "shared": false,                    // unused at runtime
  "assets": {
    "my-boxcar-body": {
      "name": "MyBoxcarBody",
      "type": "prefab",
      "filename": "Assets/.../MyBoxcarBody.prefab"
    },
    "my-boxcar-decal-tex": { "name": "...", "type": "texture", "filename": "..." }
  }
}
```

`Asset.type` follows `AssetTypeNames`: `"prefab"`, `"audio"`, `"material"`, `"texture"` (`AssetPack.Common/AssetTypeNames.cs:5`). Mapping from file extension via `AssetTypeNames.ForExtension(ext)`. **The `type` field is not enforced at load time** — `LoadAsset<T>` casts whatever the bundle returns to `T`; a wrong type → `null` and a logged exception. The catalog's `assets.ContainsKey(modelIdentifier)` is the only gate that runs on every car definition (in `DefinitionChecker.CheckCar`).

`Catalog.shared` is read **nowhere** at runtime. Don't rely on it.

### `Definitions.json`

`Container` shape (see [Car Definitions › Container](car-definitions.md#container-and-serialization)):

```jsonc
{
  "Objects": [
    {
      "Identifier": "my-boxcar-1",
      "Metadata": { "Name": "...", "Description": "...", "Tags": [], "Credits": "" },
      "Definition": {
        "Kind": "Car",                  // JsonSubtypes discriminator
        "ModelIdentifier": "my-boxcar-body",
        "Archetype": "Boxcar",
        "WeightEmpty": 50000,
        "TruckIdentifier": "standard-truck",
        "LoadSlots": [ ... ],
        "Components": [ ... ]
      }
    }
  ]
}
```

Serialization details (`Definition/Model.Definition/ContainerSerialization.cs`): Newtonsoft.Json + `CamelCasePropertyNamesContractResolver` + `Vec2Conv`/`Vec3Conv`/`QuaternionConv` for Unity vector types. **The `Identifier` and `Metadata` properties are PascalCase on the wire** (overridden via `JsonProperty(Order=…)`); inner `Definition` fields are camelCase. After deserialize, `container.Awake()` walks every item and calls `Definition.Awake()` (a no-op for `CarDefinition`).

### Catalog vs Bundle: the asset id duality

A definition references assets via two identifier dimensions:

1. `assetPackIdentifier` — which pack's bundle to open. May be `null`/empty (resolved via `ResolveAssetReference` to the contextual definition's pack).
2. `assetIdentifier` — the **bundle key** passed to `AssetBundle.LoadAssetAsync<T>`.

The `assetIdentifier` is **the AssetBundle's internal name**, not the `Catalog.assets` dictionary key. The catalog dict's keys *should* match bundle names by convention (and `DefinitionChecker.CheckCar` uses `_store.Catalog().assets.ContainsKey(modelIdentifier)` to verify), but there's no runtime enforcement — if your build pipeline puts an asset in the bundle but forgets it in `Catalog.json`, lookups still work (they just bypass validation). Conversely, an entry in `Catalog.json` not in the bundle silently passes validation but fails at load.

---

## Bootstrap: `PrefabStore.Create()`

```csharp
public static PrefabStore Create()                              // PrefabStore.cs:56
{
    PrefabStore instance = new PrefabStore();
    AddStoresFromLocation(StoreLocation.Internal);              // ← FIRST
    AddStoresFromLocation(StoreLocation.External);              // ← SECOND
    instance.LoadAssetPackStatically("shared");                 // ← preload
    instance.CheckDefinitions();                                // ← validate, drop on throw
    return instance;
}
```

Single call, no async, no events. Triggered by the lazy-init at `TrainController.PrefabStore` getter (`TrainController.cs:200`) — so the **entire pack scan happens on the first read of `TrainController.Shared.PrefabStore`**, which in vanilla is during `TrainController` startup before any car spawns. If a Harmony patch wants to mutate the store list before validation, it must run **before** the first read of that property; safe spots are `TrainController.Awake` postfix or `PrefabStore.Create` postfix.

A second `PrefabStore` instance lives in `SceneryAssetManager._prefabStore` for **edit-mode-only** access (`Application.isPlaying == false`). At runtime, `SceneryAssetManager.PrefabStore` returns `TrainController.Shared.PrefabStore` so refcounts don't fragment. Patches that introduce alternate stores must touch both paths or accept that scenery editing in Unity sees a different store than play-mode.

### `AddStoresFromLocation`

```csharp
foreach (string item in Utilities.FindAssetPacks(BasePathForLocation(location)))
    instance.AddStore(item, location);
```

Order is **`Directory.GetDirectories` order**, which on Windows/NTFS is typically alphabetical-ish but **not guaranteed**. If your mod relies on a deterministic resolution order across packs, do not depend on `_stores` insertion order — patch `AddStore` or `AssetPackContainingIdentifier` instead.

### `LoadAssetPackStatically("shared")`

```csharp
private void LoadAssetPackStatically(string storeIdentifier)    // PrefabStore.cs:73
{
    var store = _stores.FirstOrDefault(s => s.Identifier == storeIdentifier);
    if (store == null) Debug.LogWarning("Can't load asset pack statically; not found: " + ...);
    else store.LoadBundleStatic();
}
```

`AssetPackRuntimeStore.LoadBundleStatic` (`AssetPackRuntimeStore.cs:102`) calls **synchronous** `AssetBundle.LoadFromFile(AssetBundlePath)` and stores in `_staticAssetBundle`. The static bundle is **never unloaded by refcount logic** (`UnloadAssetBundleWithNoRemainingReferences` early-returns when `_staticAssetBundle != null`). Only `Dispose` releases it.

**The `shared` pack's identifier is hardcoded.** If you want a second always-resident pack, either add a custom `LoadAssetPackStatically` call (Harmony postfix on `PrefabStore.Create`) or rename your pack to `shared` (which collides with vanilla and silently replaces it — almost never what you want).

### `CheckDefinitions` — the silent-removal pattern

```csharp
private void CheckDefinitions()                                  // PrefabStore.cs:301
{
    HashSet<AssetPackRuntimeStore> hashSet = new();
    foreach (var store in _stores) {
        try {
            foreach (var obj in store.Container().Objects) {
                var checker = new DefinitionChecker(obj.Identifier, store.Identifier, store);
                checker.Check(obj.Definition);
                checker.PrintToLog();
            }
        }
        catch (Exception e) {
            Log.Error(e, "Exception while checking store {store}", store);
            hashSet.Add(store);                                  // ← entire pack queued for removal
        }
    }
    foreach (var item in hashSet) {
        Log.Warning("Removing store: {store}", item);
        _stores.Remove(item);                                    // ← silent vanish
    }
}
```

**This is the silent-removal pattern referenced in [car-definitions.md › DefinitionChecker](car-definitions.md#definitionchecker-load-time-validation), with key clarifications:**

1. **`DefinitionChecker.Check` accumulates errors/warnings into lists; it does NOT throw.** A pack with 100 broken car definitions will still be present in `_stores` after `CheckDefinitions` — the errors are logged via `Log.Error` but the cars remain queryable (and will fail at first model load with a less informative error).
2. **A pack is removed only if iterating `Container().Objects` itself throws** — i.e., a malformed `Definitions.json` that fails JSON deserialization, or an `Awake` override that throws, or a corrupt file. The first call to `store.Container()` (lazy-loaded inside the try block) is when `Definitions.json` is parsed — **a JSON syntax error in any pack's `Definitions.json` removes that pack entirely from the session**, with one `Log.Error` and one `Log.Warning`. The user sees nothing in-game.
3. The check only inspects `CarDefinition`/`SteamLocomotiveDefinition` (`DefinitionChecker.Check` (`DefinitionChecker.cs:38`)). **`SceneryDefinition`, `MaterialDefinition`, `TextureDefinition`, `TruckDefinition`, `WhistleDefinition`, `DieselLocomotiveDefinition` are validated for nothing.** A `WhistleDefinition` with empty `Audio` and `Model` will pass and only manifest at first whistle assignment.

**Patch points to make this loud:**

| Method | Why patch |
|---|---|
| `PrefabStore.CheckDefinitions` postfix | Inspect the per-pack errors/warnings via reflection on the per-pack `DefinitionChecker` — but the checkers are local-only. Easier: patch `DefinitionChecker.PrintToLog` postfix to also fire a Messenger event with `_errors`/`_warnings` for UI surfacing |
| `PrefabStore.CheckDefinitions` postfix | Compare `_stores.Count` before vs after — detect silently-removed packs |
| `DefinitionChecker.Check` prefix/postfix | Run mod-side validation against new component kinds or definition subclasses |
| `AssetPackRuntimeStore.Container` prefix | Pre-validate `Definitions.json` and surface JSON errors (vanilla's only signal is one ERROR-level log) |
| `Container.Awake` (`Definition/Model.Definition/Container.cs`) | Inject post-deserialize fixups; runs once per `Container()` first-call |

---

## `AssetPackRuntimeStore` lifecycle and refcounting

### State

```csharp
private AssetPackCatalog?    _catalog;                                  // lazy
private Container            _container;                                // lazy
private Task<AssetBundle>    _loadAssetBundleTask;                      // present iff bundle is async-loaded
private AssetBundle          _staticAssetBundle;                        // present iff LoadBundleStatic was called
private bool                 _disposed;
private readonly Dictionary<string, LoadRequest> _loadRequests = new();
```

`LoadRequest` (`AssetPackRuntimeStore.cs:23`):

```csharp
private class LoadRequest {
    public AssetBundleRequest Request { get; set; }                     // the in-flight Unity request
    public int                ReferenceCount { get; set; }
}
```

### Loading the bundle

```csharp
private Task<AssetBundle> LoadedBundle()                                // AssetPackRuntimeStore.cs:112
{
    if (_loadAssetBundleTask != null) return _loadAssetBundleTask;      // memoized
    var tcs = new TaskCompletionSource<AssetBundle>();
    if (_staticAssetBundle != null) tcs.SetResult(_staticAssetBundle);  // shared-pack fast path
    else AssetBundle.LoadFromFileAsync(AssetBundlePath).completed += (op) => {
        if (op is AssetBundleCreateRequest r && r.assetBundle != null) tcs.SetResult(r.assetBundle);
        else tcs.SetException(new Exception("Failed to load asset bundle: " + AssetBundlePath));
    };
    _loadAssetBundleTask = tcs.Task;
    return _loadAssetBundleTask;
}
```

**One bundle load per pack lifetime** (until `Dispose` or refcount-zero unload). Subsequent asset requests from the same pack share the bundle.

### Loading an asset

```csharp
public async Task<LoadedAssetReference<T>> LoadAsset<T>(string assetIdentifier, CancellationToken ct)  // :141
{
    AssetBundle assetBundle = await LoadedBundle();                     // bundle-level memoization
    ct.ThrowIfCancellationRequested();
    if (_loadRequests.TryGetValue(assetIdentifier, out var value)) {
        value.ReferenceCount++;                                         // share existing in-flight or completed request
    } else {
        var request = assetBundle.LoadAssetAsync<T>(assetIdentifier);
        ct.ThrowIfCancellationRequested();
        value = new LoadRequest { Request = request, ReferenceCount = 1 };
        _loadRequests[assetIdentifier] = value;
    }
    try {
        return new LoadedAssetReference<T>((await value.Request) as T, this, assetIdentifier);
    } catch (Exception e) {
        DecrementReferenceCount(assetIdentifier);
        throw;
    }
}
```

**Per-asset refcount.** Each `LoadAssetAsync` call returns a fresh `LoadedAssetReference<T>` (so each holder owns one count); calling `Dispose()` decrements; when **all** asset refcounts in the pack reach zero, the bundle is unloaded.

```csharp
public void DecrementReferenceCount(string identifier)                  // :247
{
    if (_disposed) return;
    if (_loadRequests.TryGetValue(identifier, out var value)) {
        value.ReferenceCount--;
        if (_loadRequests.Values.Sum(v => v.ReferenceCount) <= 0) {     // SUM, not count of zero entries
            UnloadAssetBundleWithNoRemainingReferences();
            _loadRequests.Clear();
        }
    } else {
        Log.Error("Request for identifier not found: {identifier}", identifier);
    }
}
```

**Bundle unload requires `Sum(ReferenceCount) <= 0` — not "every entry is zero."** A held entry with refcount 1 and another with refcount -1 (theoretical underflow) would also trigger unload. The check uses `Sum` directly with no clamp; over-disposing one asset can mask a held reference and prematurely unload the bundle.

```csharp
private void UnloadAssetBundleWithNoRemainingReferences()               // :269
{
    if (_staticAssetBundle != null) return;                             // ← shared/static packs never unload
    if (_loadAssetBundleTask == null) { Log.Error("ABRS Can't unload bundle - no bundle task"); return; }
    if (_loadAssetBundleTask.Result != null)
        _loadAssetBundleTask.Result.Unload(unloadAllLoadedObjects: true);   // ← UNLOAD ALL OBJECTS
    _loadAssetBundleTask.Dispose();
    _loadAssetBundleTask = null;
}
```

**`Unload(unloadAllLoadedObjects: true)` destroys every Object that was loaded from this bundle**, including any held by code that didn't go through `LoadedAssetReference` (e.g., direct Unity references to the bundle's prefabs grabbed via `Resources` or reflection). If your mod caches a `GameObject` from a non-shared pack and the refcount returns to zero, your cache becomes a dangling Unity null. Always hold the `LoadedAssetReference<T>` for the lifetime you need the asset, not the `Asset` field directly.

### `Dispose`

```csharp
public void Dispose()                                                    // :70
{
    if (_loadAssetBundleTask != null && _loadAssetBundleTask.Result != null)
        _loadAssetBundleTask.Result.Unload(unloadAllLoadedObjects: true);
    _loadAssetBundleTask?.Dispose();
    _loadAssetBundleTask = null;
    if (_staticAssetBundle != null) {
        _staticAssetBundle.Unload(unloadAllLoadedObjects: true);
        _staticAssetBundle = null;
    }
    _disposed = true;
}
```

`PrefabStore.Dispose` walks `_stores` and disposes each. **There's no game lifecycle event that disposes the `PrefabStore`** — `TrainController._prefabStore` is held for the process lifetime. The only place `PrefabStore` is disposed in vanilla is the edit-mode `SceneryAssetManager.OnDestroy`. Held assets effectively live until process exit.

### Patch candidates

| Method | Why patch |
|---|---|
| `AssetPackRuntimeStore.LoadAsset<T>` prefix | Inject mod-resolved alternative bundles, e.g., load from a streamed-from-network bundle |
| `AssetPackRuntimeStore.LoadedBundle` prefix | Replace the bundle path (e.g., test fixtures, A/B variants) |
| `AssetPackRuntimeStore.UnloadAssetBundleWithNoRemainingReferences` prefix | Veto unload — keep all bundles resident if RAM is cheap and load latency hurts |
| `AssetPackRuntimeStore.LoadBundleStatic` postfix | Track which packs are made resident |
| `AssetPackRuntimeStore.DecrementReferenceCount` prefix | Detect over-dispose patterns; `value.ReferenceCount < 0` after decrement is a bug |
| `AssetPackRuntimeStore.SaveContainer` (`:236`) | Throws if `Location != External` — patch to allow writing to internal packs (dangerous but required for some mod authoring tools) |
| `AsyncExtensionMethods.GetAwaiter` for `AssetBundleRequest` (`AsyncExtensionMethods.cs:27`) | Override the await contract — if `request.asset` is null, vanilla throws `Exception("Failed to load asset from asset bundle")`. Catch to provide a fallback asset |

---

## `PrefabStore` — aggregator and identifier resolver

### Resolution order: `_stores` is a `List`, walked first-to-last

```csharp
private readonly List<AssetPackRuntimeStore> _stores = new();

private AssetPackRuntimeStore AssetPackForIdentifier(string assetPackIdentifier)         // :131
{
    for (int i = 0; i < _stores.Count; i++)
        if (_stores[i].Identifier == assetPackIdentifier) return _stores[i];
    throw new Exception("No store with identifier " + assetPackIdentifier);
}

private AssetPackRuntimeStore AssetPackContainingIdentifier(string identifier)            // :144
{
    for (int i = 0; i < _stores.Count; i++)
        if (_stores[i].ContainsIdentifier(identifier)) return _stores[i];
    throw new UnknownIdentifierException(identifier);
}
```

**The order in `_stores` IS the resolution priority.** Insertion order is:

1. Internal packs first (filesystem order from `Directory.GetDirectories` of `StreamingAssets/AssetPacks/`).
2. External packs second (filesystem order of `persistentDataPath/AssetPacks/`).
3. Plus same-id replacements (see `AddStore` below) — replacements are **in place**, not appended, so they keep their original index.

`AssetPackForIdentifier` is used when the consumer already knows which pack (`AssetReference.AssetPackIdentifier` is non-null). `AssetPackContainingIdentifier` is the by-definition-id lookup — **the first pack containing the identifier wins**, all later packs with the same identifier are shadowed.

### `AddStore` — the documented mod-override mechanism

```csharp
private void AddStore(string storeIdentifier, StoreLocation location)                     // PrefabStore.cs:86
{
    AssetPackRuntimeStore newStore = new AssetPackRuntimeStore(storeIdentifier, location);
    for (int i = 0; i < _stores.Count; i++) {
        var existing = _stores[i];
        if (existing.Identifier == storeIdentifier) {
            if (location == StoreLocation.External)
                Log.Information("Replace Store {location}: {identifier}", location, storeIdentifier);
            existing.Dispose();
            _stores[i] = newStore;                          // ← in-place replacement
            return;
        }
    }
    if (location == StoreLocation.External)
        Log.Information("Add Store {location}: {identifier}", location, storeIdentifier);
    _stores.Add(newStore);
}
```

**Three pivotal facts about `AddStore`:**

1. **Same-pack-identifier collision: `Dispose()` then `_stores[i] = newStore`.** The old pack's bundle is unloaded (with `unloadAllLoadedObjects:true`), then the new pack takes the same slot. This is the [car-definitions.md](car-definitions.md#end-to-end-mod-patterns) "Replacing a vanilla car" mechanism — drop a folder named after a vanilla pack into `External/AssetPacks/` and your pack wholesale replaces it. **The whole pack is replaced, not individual definitions** — anything in the vanilla pack you didn't re-ship is gone.
2. **Order matters because `Internal` is added first.** When `External` adds a same-id pack, the loop finds the existing Internal store and replaces it at the **same index** (front of the list). So External wins over Internal not by being late but by being a same-id replacement. **Cross-id collisions** (different pack names contain the same definition id) are governed by walk order, which means Internal still wins because Internal was added first.
3. **Logging is asymmetric: only External adds/replaces are logged.** Internal-Internal collisions (theoretically possible if you somehow have duplicate folder names — e.g., different StreamingAssets layouts in dev) are silent. Mods scanning the log for "Replace Store" entries will miss any replacement chain happening among Internal packs.

**To override a *single* definition without replacing the whole pack**, you have to win the resolution race in `AssetPackContainingIdentifier`. Two practical paths:

- **Reorder `_stores`.** Patch `PrefabStore.Create` postfix and reflect to mutate `_stores`, moving your pack to index 0. Now your identifier resolves before any other pack with the same id. Caveat: any *asset* references made via `AssetReference{AssetPackIdentifier=null}` from a non-overridden definition will still resolve via `ResolveAssetReference` to the **original** definition's pack (which would now be your pack if you took over the identifier — but those assets must exist in your bundle).
- **Patch `AssetPackContainingIdentifier`** to map specific identifiers to specific packs. This is the cleanest single-definition override: prefix-match a name list and return your store directly, falling through to the original implementation otherwise.

**There is no `RemoveStore` API.** Mods cannot un-register a pack at runtime without reflection. `Dispose` exists per-store but `_stores` membership is `private` and only mutated by `AddStore` and `CheckDefinitions`-removal.

### `LoadAssetAsync<T>` — the consumer entry point

```csharp
public async Task<LoadedAssetReference<T>> LoadAssetAsync<T>(string assetPackIdentifier,
                                                              string assetIdentifier,
                                                              CancellationToken ct) where T : Object
{
    if (string.IsNullOrEmpty(assetPackIdentifier)) throw new ArgumentException(...);
    if (string.IsNullOrEmpty(assetIdentifier))     throw new ArgumentException(...);
    var loaded = await AssetPackForIdentifier(assetPackIdentifier).LoadAsset<T>(assetIdentifier, ct);
    if (CarShaderHelper.Instance != null)
        CarShaderHelper.Instance.ReplaceShaders(loaded.Asset);              // ← shader pass on every load
    return loaded;
}
```

**Every loaded asset goes through `CarShaderHelper.ReplaceShaders` if the helper exists.** That's a per-load operation that walks renderers/materials and remaps shaders to the project's runtime shader variants. Patches that bypass `LoadAssetAsync` (e.g., a direct `AssetBundle.LoadAsset` call to your bundle) will skip this and your custom car material may fall back to the magenta shader-error material in built players.

`AssetPackForIdentifier` throws plain `Exception("No store with identifier ...")` if the pack name is wrong — **not** `UnknownIdentifierException`. The two failure modes (unknown definition vs unknown pack) have different exception types but neither is caught generically: the `LoadModelsAsync` caller catches `Exception` (`Car.cs:1174`), logs, and bails. The car simply has no body model.

### `ResolveAssetReference` — null-pack handling

```csharp
public AbsoluteAssetReference ResolveAssetReference(string contextualDefinitionIdentifier,
                                                     AssetReference assetReference)         // :292
{
    if (string.IsNullOrEmpty(assetReference.AssetPackIdentifier))
        return new AbsoluteAssetReference(
            AssetPackIdentifierContainingDefinition(contextualDefinitionIdentifier),
            assetReference.AssetIdentifier);
    return new AbsoluteAssetReference(assetReference.AssetPackIdentifier,
                                       assetReference.AssetIdentifier);
}
```

When an asset reference omits `AssetPackIdentifier`, the resolver fills in the pack of the *containing* definition. So a `LoadModelComponent` inside `pack-a/Definitions.json` referencing `AssetIdentifier="my-load-model"` with no pack id will look in `pack-a/Bundle`. If you override the containing definition by reordering stores so `pack-b/Definitions.json` provides the same identifier, asset references with null `AssetPackIdentifier` now resolve to `pack-b` — which only works if `pack-b/Bundle` actually contains `my-load-model`.

### `AllCarDefinitionInfos` and `AllDefinitionInfosOfType<T>` — dedupe by identifier

```csharp
public IEnumerable<TypedContainerItem<CarDefinition>> AllCarDefinitionInfos {
    get {
        HashSet<string> hashSet = new HashSet<string>();
        foreach (var store in _stores)
            foreach (var obj in store.Container().Objects)
                if (obj.Definition is CarDefinition)
                    hashSet.Add(obj.Identifier);                            // dedupe across packs
        return hashSet.Select(CarDefinitionInfoForIdentifier);              // each id resolved via _stores order
    }
}
```

The HashSet ensures each identifier appears once. `CarDefinitionInfoForIdentifier` uses `AssetPackContainingIdentifier` which is order-dependent — so the visible definition is **the first pack's version**. Shadowed definitions never appear in any UI: Placer, EquipmentWindow, CarEditor all walk through `AllCarDefinitionInfos`.

### `TruckPrefabForId` — long-lived references

```csharp
private readonly Dictionary<string, Task<Wheelset>> _truckPrefabTasks = new();
private readonly HashSet<LoadedAssetReference<GameObject>> _truckReferences = new();

public Task<Wheelset> TruckPrefabForId(string truckIdentifier)               // :157
{
    if (_truckPrefabTasks.TryGetValue(truckIdentifier, out var existing)) return existing;
    var tcs = new TaskCompletionSource<Wheelset>();
    var task = tcs.Task;
    _truckPrefabTasks[truckIdentifier] = task;
    LoadWheelset(truckIdentifier, tcs);                                       // fire-and-forget async
    return task;
}

private async void LoadWheelset(string truckIdentifier, TaskCompletionSource<Wheelset> tcs)
{
    // ... loads truck prefab, builds Wheelset, attaches Animator
    tcs.SetResult(wheelset);
    _truckReferences.Add(loadedAssetReference);                               // ← never released
}
```

**Trucks are loaded once and held for the `PrefabStore` lifetime.** The `LoadedAssetReference<GameObject>` lives in `_truckReferences` until `PrefabStore.Dispose` (effectively process exit). The pack containing the truck definition stays loaded forever once any car using it spawns. This is intentional (trucks are tiny, ubiquitous, and reload-thrashing them on every model unload would be wasteful) but means **a mod that ships a 2 GB pack with a single new truck definition will hold that bundle resident forever** if any car uses it.

### Patch candidates (PrefabStore)

| Method | Why patch |
|---|---|
| `PrefabStore.Create` (static) postfix | Inject extra packs, reorder `_stores`, install a custom `LoadAssetPackStatically` for second always-resident pack |
| `PrefabStore.AddStore` prefix/postfix | Veto a replacement, log mod-side, or rewrite the pack identifier |
| `PrefabStore.AssetPackContainingIdentifier` prefix | **The single most useful patch point for identifier-resolution-order injection.** Map specific identifiers to specific stores before fall-through |
| `PrefabStore.AssetPackForIdentifier` prefix | Map a pack identifier to a different store (pack alias) |
| `PrefabStore.LoadAssetAsync<T>` prefix | Intercept by `(packId, assetId)` pair to substitute a runtime-built asset (e.g., procedural mesh, dynamic texture) |
| `PrefabStore.ResolveAssetReference` prefix | Force `AssetPackIdentifier` for null-pack references — useful when reordering stores would otherwise misroute internal asset refs |
| `PrefabStore.AllCarDefinitionInfos` getter postfix | Inject synthetic car definitions not backed by any asset pack — but **`AssetPackIdentifierContainingDefinition` will throw later in `Car.LoadModelsAsync`**, so you must also patch `AssetPackContainingIdentifier` to redirect to a real pack containing the model. Also see `AllDefinitionInfosOfType<T>` for parallel surface |
| `PrefabStore.CheckDefinitions` postfix | Surface silent pack-removal as a UI alert |

---

## Async loading patterns

The async surface is uniform: every consumer awaits `IPrefabStore.LoadAssetAsync<T>(packId, assetId, ct)` and holds the returned `LoadedAssetReference<T>` for the lifetime of usage.

### `Car.LoadModelsAsync` — the primary consumer

```csharp
private async void LoadModelsAsync()                                          // Car.cs:1160
{
    try {
        IPrefabStore prefabStore = TrainController.PrefabStore;
        string modelIdentifier = Definition.ModelIdentifier;
        string assetPackIdentifier = prefabStore.AssetPackIdentifierContainingDefinition(DefinitionInfo.Identifier);
        _modelLoadTasks["model"] = prefabStore.LoadAssetAsync<GameObject>(assetPackIdentifier, modelIdentifier, CancellationToken.None);
        if (!string.IsNullOrEmpty(Definition.TruckIdentifier))
            _truckPrefabLoadTask = prefabStore.TruckPrefabForId(Definition.TruckIdentifier);
        await Task.WhenAll(_modelLoadTasks.Values);
    } catch (Exception e) {
        Log.Error(e, "Error loading car model {identifier}", DefinitionInfo.Identifier);
        return;                                                              // ← silent giveup on car
    }
    try { if (_truckPrefabLoadTask != null) await _truckPrefabLoadTask; }
    catch (Exception e) { Log.Error(e, "Error loading trucks"); }
    HandleModelsLoaded();
}
```

**`CancellationToken.None`** — car model loads cannot be cancelled. If a car is unloaded while its model is loading, `HandleModelsLoaded` runs anyway, and the early-return guards (`!_modelLoadPending`, `this == null`) catch it. The `LoadedAssetReference` produced by the cancelled load is held in `_modelLoadTasks` and disposed in `UnloadModels` (`Car.cs:1556-1559`).

The car's culling-band-driven `ModelLoadRetain` (`Car.cs:1093`) increments a token-counted retain; the **final** release schedules `UnloadModelsDelayed` with `Config.Shared.carModelUnloadDelay` (vanilla default **300 seconds**, `Config.cs:99`). This is the dominant retention pressure on non-shared bundles — a car that the player drives past then 6 minutes later the bundle unloads.

### `WhistleController.Configure` — async with cancellation

```csharp
private async void Configure(WhistleCustomizationSettings settings)            // WhistleController.cs:79
{
    if (_loadCancellationTokenSource != null) {
        _loadCancellationTokenSource?.Cancel();
        _loadCancellationTokenSource = null;
    }
    if (_whistleModel != null) { Object.Destroy(_whistleModel); _whistleModel = null; }
    DisposeModelLoadReference();

    string whistleIdentifier = settings.WhistleIdentifier;
    IPrefabStore prefabStore = TrainController.Shared.PrefabStore;
    var whistleDefinition = prefabStore.DefinitionForIdentifier<WhistleDefinition>(whistleIdentifier, out _);

    if (!whistleDefinition.Model.IsEmpty) {
        _loadCancellationTokenSource = new CancellationTokenSource();
        var token = _loadCancellationTokenSource.Token;
        var assetReference = prefabStore.ResolveAssetReference(whistleIdentifier, whistleDefinition.Model);
        try {
            _modelLoadReference = await prefabStore.LoadAssetAsync<GameObject>(assetReference, token);
        } catch (OperationCanceledException) { return; }
        if (this == null) return;                                              // self-destruct check
        _whistleModel = Object.Instantiate(_modelLoadReference.Asset, base.transform, false);
    }
    if (!whistleDefinition.Audio.IsEmpty) {
        _loadCancellationTokenSource = new CancellationTokenSource();         // ← NEW token, NOT additional cancel
        var token2 = _loadCancellationTokenSource.Token;
        var assetReference2 = prefabStore.ResolveAssetReference(whistleIdentifier, whistleDefinition.Audio);
        _audioLoadReference = await prefabStore.LoadAssetAsync<AudioClip>(assetReference2, token2);
        // ...
    }
}
```

Cross-link to [audio.md › `WhistlePlayer`](audio.md#audiowhistleplayer-audiowhistleplayercs--steam-whistle) — `WhistleController` is the primary audio consumer of asset packs, observing the `whistle.custom` KVO key and async-loading both a model `GameObject` and an `AudioClip`.

**Race conditions to know:**

1. **Two rapid customizations in flight cancel via `_loadCancellationTokenSource`**, but only the **first** await (the model load) handles `OperationCanceledException` cleanly. The audio load's `await` doesn't have the catch — if cancelled mid-audio-load it propagates as an unobserved exception. The control flow assumes audio loads complete fast enough that this doesn't matter; in practice it's true because the asset is already in cache after the model load triggered the bundle to load.
2. **`if (this == null)` checks Unity's lifetime null** — the GameObject can be destroyed mid-await. Note this only checks **after** the model load; if the GameObject is destroyed between the model assignment and the audio await, the next `whistlePlayer.Configure(_audioLoadReference.Asset)` call will NRE because `whistlePlayer` is a serialized field on the destroyed component.
3. **The `_modelLoadReference` is held until the next `Configure` or `OnDestroy` calls `DisposeModelLoadReference`.** A mod that swaps whistle assets rapidly will refcount-thrash the pack — this is fine, the bundle stays loaded until the refcount truly reaches zero.

### `CarLoadModelController.Configure` — list of references

```csharp
public async void Configure(int slotIndex, string loadIdentifier,
                             List<AssetReference> modelAssetReferences,
                             List<PositionRotationScale> instancePositions)   // CarLoadModelController.cs:60
{
    _modelLoadCancellationTokenSource = new CancellationTokenSource();
    var ct = _modelLoadCancellationTokenSource.Token;
    var prefabStore = TrainController.Shared.PrefabStore;
    _modelLoadReferences.Clear();
    foreach (var modelAssetReference in modelAssetReferences) {
        var item = await prefabStore.LoadAssetAsync<GameObject>(
            modelAssetReference.AssetPackIdentifier, modelAssetReference.AssetIdentifier, ct);
        if (this == null) { Log.Warning("CarLoadModelController destroyed while loading model."); return; }
        _modelLoadReferences.Add(item);
    }
    StartObserving();
}
```

**Sequential await per asset** — N references = N round-trips. On a slow disk, a large `LoadModels` list serializes into N frame-deferred bundle loads. Patch to `Task.WhenAll` if you need parallelism. **Cancellation is via the same token** — cancelling between iterations leaves a partial `_modelLoadReferences` that **`OnDestroy` doesn't dispose** if the destroy happens before any iteration completes (the early-return doesn't release in-progress refs). Minor leak but not catastrophic — the next refcount-zero on the same asset triggers cleanup if pack ever unloads.

### `BuilderPhotoController` and `EquipmentWindow` — UI-driven loads

`RollingStock/BuilderPhotoController.cs:57` and `UI.Equipment/BuilderPhoto.cs` use the same pattern for the equipment-purchase preview thumbnail. These are short-lived loads driven by UI selection; refcount goes back to zero when the panel closes.

### Patch candidates (Async loading)

| Method | Why patch |
|---|---|
| `Car.LoadModelsAsync` prefix | Substitute the async `Task<LoadedAssetReference<GameObject>>` for a custom source — e.g., a procedurally-generated model |
| `Car.LoadModelsAsync` postfix | Run mod-side post-load (additional asset loads parented to the loaded body) — but `_modelLoadTasks` is private; use `HandleModelsLoaded` postfix instead |
| `Car.HandleModelsLoaded` postfix | Cleaner injection point for "after body is instantiated" — `BodyTransform` is non-null and components are about to setup |
| `Car.UnloadModels` prefix | Catch model-bundle releases; useful for mod-side caches keyed off body refs |
| `WhistleController.Configure(WhistleCustomizationSettings)` | Replace the asset-loading path entirely. See [audio.md › patch candidates](audio.md#whistlecontroller-rollingstocksteamwhistlecontrollercs--steam-whistle) |
| `CarLoadModelController.Configure` | Add parallelism, cache by hash, or substitute asset sources |
| `AsyncExtensionMethods.GetAwaiter(AssetBundleRequest)` | Custom retry/fallback for failed asset loads |

---

## Identifier resolution order — what mods must know

The full priority chain a definition identifier traverses to become a loaded asset:

```
identifier (string)
   │
   ▼
PrefabStore.AssetPackContainingIdentifier(identifier)
   │  for (i = 0; i < _stores.Count; i++)
   │      if _stores[i].ContainsIdentifier(identifier) return _stores[i]
   │  throw UnknownIdentifierException
   │
   │  ── _stores order (vanilla):
   │     1..N  Internal packs in Directory.GetDirectories order
   │     N+1..M  External packs in Directory.GetDirectories order
   │     same-id replacement: REPLACES at original index
   │
   ▼
AssetPackRuntimeStore.ContainerItemForObjectIdentifier(identifier)
   │  linear scan over Container().Objects
   │
   ▼
ContainerItem.Definition (cast to expected ObjectDefinition subclass)
   │
   ▼
Component-driven loads use AssetReference{AssetPackIdentifier, AssetIdentifier}
   │  if (AssetPackIdentifier == null) → ResolveAssetReference fills with identifier's pack
   │
   ▼
AssetPackForIdentifier(packId).LoadAsset<T>(assetId, ct)
   │  bundle-load via AssetBundle.LoadFromFileAsync (memoized)
   │  asset-load via assetBundle.LoadAssetAsync<T>(assetId)
   │
   ▼
T (cast may return null silently if type mismatches)
```

**The four resolution decisions a mod can intercept:**

| Decision | Where | Patch hook |
|---|---|---|
| Which pack contains a definition | `AssetPackContainingIdentifier` | Patch this method directly |
| Which pack a same-id collision wins | `AddStore` | Patch in `Create` postfix to reorder `_stores` |
| Which pack a null-`AssetPackIdentifier` resolves to | `ResolveAssetReference` | Patch directly |
| Which bundle a packId resolves to | `AssetPackForIdentifier` | Patch directly |

**Two non-obvious failure modes:**

- **`AssetPackForIdentifier` throws plain `Exception`** when a known asset reference points at a non-existent pack id. Different exception type from the unknown-definition case, breaks generic catches that look for `UnknownIdentifierException`.
- **`LoadAsset<T>` casts via `as T`**, returning null for type mismatch instead of throwing. So `LoadAsset<GameObject>("my-audio-id")` returns a `LoadedAssetReference<GameObject>` whose `.Asset` is null — the consumer NREs at the next access. The implicit contract is "the catalog entry's `type` field tells you the right T," but vanilla doesn't enforce this.

---

## MP — packs are local-only, mismatches degrade silently

**There is NO asset pack synchronization between host and client.** Each peer scans its own filesystem at startup. The wire format references definitions only by string `prototypeId` (snapshot) and string `identifier` (PropertyChange/spawn messages). No catalog hash, no version negotiation, no pre-flight validation that the client has the packs the host needs.

Cross-link to [multiplayer-core.md](multiplayer-core.md) and [save-load.md](save-load.md).

### What actually happens on mismatch

1. **Host has a pack the client doesn't (e.g., a custom car):**
   - Snapshot includes a `Snapshot.Car{ prototypeId="host-only-pack:my-boxcar" }`.
   - Client receives in `HandleSnapshotCars` (`TrainController.cs:1553`).
   - Inside the per-car loop, `AddCarInternal` calls `PrefabStore.CarDefinitionInfoForIdentifier(prototypeId)`.
   - Throws `PrefabStore.UnknownIdentifierException`.
   - Caught at `TrainController.cs:1576`:
     ```csharp
     catch (PrefabStore.UnknownIdentifierException ex) {
         Log.Error(ex, "Car definition unknown: {car}; car will be missing. {e}", value3, ex);
         RemoveCars(new List<string> { value3.id });          // ← client tells the host the car is gone (?!)
         dictionary2[value3.id] = value3.ToString();          // tracked as "lost"
     }
     ```
   - **`RemoveCars` is sent as a request message, but with Trainmaster auth — a non-Trainmaster client will fail this auth check silently.** The car is removed from the *client's* lookup either way (via `RemoveCars` local apply path) but the host keeps it.
   - Result: client sees no car at that location, host still has it, future PropertyChanges for that car id will be applied to client KVO of a non-existent car (KVO has no validation). On next snapshot the same `UnknownIdentifierException` fires again and the same removal cycle repeats.

2. **Client has a pack the host doesn't:**
   - Effectively a no-op — clients don't push car definitions to the host. The client may try to spawn a car via `RequestPurchaseEquipment`, the host receives the message, looks up the prototype identifier, throws `UnknownIdentifierException` (uncaught in the `EquipmentPurchase` path at `Game.State/EquipmentPurchase.cs:99`), the request silently fails.

3. **Both have the pack but different versions (different `Definitions.json` content):**
   - Each peer uses its local copy of the definition. Cars spawn fine. **But properties driven by `Definition` fields desync.** Examples: `Definition.WeightEmpty` (host's value drives `Car.Weight` for physics, client computes its own visual mass), `Definition.LoadSlots.Count` (host registers N observers, client registers M observers, snapshots include `load.{i}` for indices the other side ignores). Air-pressure/coupling/integration-set physics are host-driven so the gameplay state is consistent, but client-side visuals diverge.
   - **No detection mechanism exists.** The user sees mysterious physics/visual drift.

4. **Identifier collision but different content (rare but possible if two mods use the same id):**
   - Each peer resolves to whichever pack is first in their `_stores`. Host and client disagree on what "boxcar-1" *is*. Same as case 3.

### What does NOT happen

- No pre-game lobby check of installed packs.
- No catalog-hash exchange.
- No "missing pack" UI surface.
- No negotiation — host doesn't downgrade to client's pack subset, client doesn't request packs from host.
- **No streaming of asset bundles over the wire.** The Steam P2P channels (see [multiplayer-core.md](multiplayer-core.md)) carry MessagePack-encoded messages and PropertyChange events; bundles are filesystem-resident or absent.

### Patch points for MP-aware pack sync (mod territory)

| Goal | Patch surface |
|---|---|
| Detect pack mismatch on connect | Hook `Multiplayer.Client.ConnectionEstablished` (or equivalent, see [multiplayer-core.md](multiplayer-core.md)). Send a custom IGameMessage carrying a list of `(packId, contentHash)` tuples. Compare on host. Disconnect on mismatch with a UI alert |
| Surface "car will be missing" to UI | Patch `TrainController.HandleSnapshotCars` or `TrainController.cs:1576` `UnknownIdentifierException` handler postfix to fire a Messenger event the UI subscribes to |
| Stream a missing pack from host to client | Build a custom request/response message pair carrying chunked bundle bytes + Catalog/Definitions JSON. After write to disk, patch `PrefabStore.AddStore` + reflection on `_stores` to add the new pack mid-session **without** disposing existing references (currently impossible without re-architecting because `AddStore` is private) |
| Block car spawn on unsupported pack | Patch `Car.LoadModelsAsync` prefix to emit a Messenger event with the missing identifier; also patch `EquipmentPurchase.CarDescriptorsFromRequest` to validate identifiers exist before the purchase commits |

**Mid-session pack add is largely unsupported by vanilla** — `_truckReferences` and other long-lived holders, plus the `_carCuller` already retaining `LoadedAssetReference`s, mean that swapping pack contents while the world is running is fragile. Mods doing this should consider a save-and-reload boundary instead.

---

## Cross-cutting types

| Type | File | Used by |
|---|---|---|
| `LoadedAssetReference<T>` | `AssetPack.Runtime/LoadedAssetReference.cs` | Every consumer; RAII handle |
| `AbsoluteAssetReference` | `Definition/Model.Definition.Data/AssetReference.cs` | Resolved (pack, asset) tuple — see [car-definitions.md](car-definitions.md#asset-references) |
| `AssetReference` | same | Definition-side reference; nullable pack |
| `AnimationReference` / `MaterialReference` / `TransformReference` | `Definition/Model.Definition.Data/` | Name-based references resolved by `AnimationMap`/`MaterialMap` MonoBehaviours on body prefab |
| `Container` / `ContainerItem` / `ObjectMetadata` | `Definition/Model.Definition/` | The deserialized `Definitions.json` shape — see [car-definitions.md › Container](car-definitions.md#container-and-serialization) |
| `IPropertyValue` (KVO) | `KeyValue.Runtime/` | Snapshot per-car properties; orthogonal to assets but cross-references via `prototypeId` lookup |

---

## Gotchas

- **`shared` is hardcoded.** The string `"shared"` appears as a string literal in `PrefabStore.Create()` — there's no constant. Adding a second always-resident pack requires a Harmony patch on `PrefabStore.Create` postfix to call `LoadAssetPackStatically` for your pack.
- **Static bundle never unloads via refcount.** A pack loaded by `LoadBundleStatic` ignores `UnloadAssetBundleWithNoRemainingReferences` (early return at `:271`). Only `Dispose` releases it. If `shared` accidentally swells to 1 GB, you eat that until process exit.
- **`Catalog.json` parse error is fatal to that pack but loud only as one `Log.Error`.** No UI surface, no aggregated startup error. Mods that expect to be loaded should validate their `Catalog.json` at build time.
- **`Definitions.json` parse error removes the entire pack from `_stores`.** Even if 99 of 100 definitions are valid, a single trailing comma kills all of them. Patch `CheckDefinitions` to surface this loudly.
- **`DefinitionChecker` checks only `CarDefinition` and `SteamLocomotiveDefinition`.** Scenery, materials, textures, trucks, whistles, diesels — no validation. A `DieselLocomotiveDefinition` with an empty `ModelIdentifier` passes silently.
- **`DefinitionChecker.Check` accumulates — it does not throw.** A pack with errors stays loaded; only iteration-failure pulls a pack. The expectation is that errors will manifest as log spam and consumer-side failures (`Car.LoadModelsAsync` Exception, `WhistleController.Configure` NRE, etc.).
- **External vs Internal logging asymmetry.** `AddStore` only logs for `External` location. Internal-Internal collisions (folder duplication in dev StreamingAssets) are silent. Use a `PrefabStore.Create` postfix to dump `_stores.Select(s => $"{s.Location}:{s.Identifier}")` if debugging resolution.
- **The first read of `TrainController.PrefabStore` runs the whole bootstrap.** This is a synchronous-on-first-read property. Thread/timing-sensitive Harmony patches that touch packs need to either run before this read or accept that the scan has already happened.
- **`LoadAsset<T>` returns `null` `Asset` on type mismatch** instead of throwing. The implicit contract that catalog `type` matches the load-site `T` is unenforced — patch `AssetPackRuntimeStore.LoadAsset<T>` postfix to validate.
- **`UnloadAssetBundleWithNoRemainingReferences` uses `Sum(ReferenceCount) <= 0`** — over-dispose can underflow into negatives and trigger premature unload. Patch `DecrementReferenceCount` to clamp.
- **`Unload(unloadAllLoadedObjects: true)` is destructive.** Any non-`LoadedAssetReference` cache of bundle objects becomes a Unity null. Always hold the reference, not the asset.
- **Trucks are held forever.** Pack containing a `TruckDefinition` referenced by any spawned car will never unload its bundle. Co-locate trucks in `shared` if memory matters.
- **Edit-mode `SceneryAssetManager` constructs a separate `PrefabStore`.** Refcounts are separate. In editor preview vs runtime, the same asset has different lifetimes.
- **No `RemoveStore` API.** Mods cannot dynamically un-register a pack without reflection.
- **MP packs do not sync.** Each peer scans its own disk. Mismatches manifest as silent car removals on snapshot load and silent purchase failures.
- **Snapshot save embeds `prototypeId` strings.** A save written with mod packs installed and loaded without them will silently drop those cars (the same `UnknownIdentifierException` handler at `TrainController.cs:1576` runs, log-only). The save file is preserved; reinstalling the packs restores the cars on next load.
- **`Catalog.shared` field is dead.** Ignore. Pack residency is decided by `LoadAssetPackStatically`, not the catalog.
- **`Catalog.identifier` and `Catalog.name` are also unused at runtime.** The directory name is the pack id. The `name` field is purely for tooling.
- **`AssetPackRuntimeStore.SaveContainer` throws on `Internal` location.** Mods that want to mutate vanilla pack JSON cannot do so via this API; either replace the pack via External or write to disk directly.
- **`LoadModelComponent.Models` is `List<AssetReference>` and `CarLoadModelController.Configure` awaits sequentially.** N-asset cargo configs serialize their loads. Negligible for typical car loads (1-3 entries) but a mod with 30 cargo variants per slot will have visible startup latency.
- **The `WhistleController` cancellation pattern doesn't catch the audio-load `OperationCanceledException`** (only the model load is wrapped in try/catch). A rapid-fire whistle change can yield an unobserved exception in logs.
- **`PrefabStoreExtensions.Random` (used by industries to order cars) walks `AllCarDefinitionInfos`** — a mod that registers many synthetic definitions widens the random pool for all industries. Use `CarTypeFilter` carefully if mod-shipping definitions of unusual `CarType`s.

---

## Cross-references

- **Definition layer** — `ObjectDefinition`, `CarDefinition`, `Component`, `IComponentBuilder`, `Container`, `ContainerSerialization`: see [car-definitions.md](car-definitions.md).
- **Snapshot/save lookup of `prototypeId`** — see [save-load.md](save-load.md) and [cars-cargo.md › Lifecycle spine](cars-cargo.md#lifecycle-spine).
- **MP context** — host-authoritative state, no pack sync, snapshot-based late-join: see [multiplayer-core.md](multiplayer-core.md).
- **Whistle async loading example** — `WhistleController.Configure` consumes `IPrefabStore` for `whistle.custom` KVO-driven asset loads: see [audio.md › `WhistleController`](audio.md#audiowhistleplayer-audiowhistleplayercs--steam-whistle).
- **Scenery/map asset loading** — `SceneryAssetManager` parallel store and the broader data-driven content pattern: see [map-mods-vanilla-survey.md](../map-mods-vanilla-survey.md).
- **`CarLoadModelController` cargo visualization** — async load list per slot: see [cars-cargo.md › Visual loading](cars-cargo.md#visual-loading-carloadmodelcontroller-and-friends).
- **Truck prefab caching** — `TruckPrefabForId` permanent retention: cross-link to [car-definitions.md › TruckDefinition](car-definitions.md#known-objectdefinition-subclasses).
