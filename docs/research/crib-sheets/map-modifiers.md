# Map Modifiers — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Map.Runtime/`, `Railroader-ILSPY/Assembly-CSharp/`, `Railroader-ILSPY/Definition/`)
**Companions:** [Tile Loading & Bardo](tile-loading-bardo.md), [Track Topology](track-topology.md), [Floating Origin](floating-origin.md), [Map Mods Vanilla Survey](../map-mods-vanilla-survey.md)

The map-modifier system is how everything that *isn't baked PNG terrain data* — track roadbed cuts/fills, vegetation clearing under bridges, water carving for rivers, tunnel mesh holes, scenery footprints — gets baked into a tile at the moment it's built. There are exactly **three concrete modifier types** (`HeightmapModifier`, `MaskModifier`, `TunnelModifier`), all implementing `IMapModifier` and all keyed by a GUID inside three parallel `ModifierStorage<T>` AABB-tree-indexed dictionaries on `MapManager`. Modifiers are **per-machine, never replicated, never persisted**; they re-register on every scene load via `MaskComponentBase.OnEnable` (which is implicit because every consumer is a `MonoBehaviour`-attached `StaticMapMask` or `MapMaskBase` subclass placed in the map scene). Tile rebuild is the *only* evaluation point — the modifier list is consulted during `TerrainBuilder.BuildTerrain`, baked into a heightmap RenderTexture via the `Hidden/Railroader/MaskToSplat` + `Hidden/Railroader/HeightToSlope` shader pipeline plus the `fillCutMaterial` (an SDF carve shader), then committed to the `TerrainData`. Re-trigger comes from `MapManager.Invalidate(Bounds, 0.5f)` whose 500ms debounce coalesces frame-burst registrations into a single rebuild pass. There is **no MP synchronization, no save/load record, no per-tile modifier diff** — clients see whatever modifiers their local scene's MonoBehaviours register at scene-load time. Mods that author terrain authoring code at runtime (post-scene-load) will diverge from clients unless they also run identically on every machine.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `IMapModifier` | `Map.Runtime/IMapModifier.cs:5` | Marker interface: `IMaskDescriptor Mask` + `OffsetBy(Vector3)` |
| `IMapModifierSource` | `Map.Runtime/IMapModifierSource.cs:7` | The "give me modifiers overlapping this tile" interface; `MapManager` is the only impl |
| `HeightmapModifier` | `Map.Runtime.MapModifiers/HeightmapModifier.cs:5` | Mutable struct with `Order` + `Kind` (BlendHeight/Roadbed/Min/Max) + `Mask` |
| `MaskModifier` | `Map.Runtime.MapModifiers/MaskModifier.cs:5` | Readonly struct: writes a `MaskName` channel (Track/Water/Tree/CutTrees/Object/Dirt) at value 0..1 |
| `TunnelModifier` | `Map.Runtime.MapModifiers/TunnelModifier.cs:5` | Carves holes (terrain mesh) via `TerrainData.SetHoles`, curve-defined |
| `MaskName` (enum) | `Map.Runtime.MapModifiers/MaskName.cs:3` | `Track, Water, Tree, CutTrees, Object, Dirt` (exactly 6) |
| `HeightmapModifierKind` (enum) | `Map.Runtime/HeightmapModifierKind.cs:3` | `BlendHeight, Roadbed, Min, Max` (exactly 4) |
| `MapManager.AddModifier(IMapModifier, CoordinateSystem)` | `Map.Runtime/MapManager.cs:978` | Returns GUID; auto-invalidates intersecting tiles after 0.5s |
| `MapManager.RemoveModifier(string)` | `Map.Runtime/MapManager.cs:1009` | Symmetric remove + invalidate |
| `MaskComponentBase` | `Map.Runtime.MaskComponents/MaskComponentBase.cs:6` | Abstract MonoBehaviour wrapper: `OnEnable`/`OnDisable`/`OnValidate` lifecycle |
| `StaticMapMask` | `Map.Runtime.MaskComponents/StaticMapMask.cs:9` | Direct-add-modifier MonoBehaviour for code-driven sites (track, telegraph) |
| `MapMaskBase` | `Map.Runtime.MaskComponents/MapMaskBase.cs:8` | Authored-in-scene/in-editor base for `Circle`/`Rectangle`/`Curve`/`RiverPath` masks |
| `TerrainBuilder.BuildTerrain` | `Map.Runtime/TerrainBuilder.cs:142` | The pipeline. Reads modifiers in `Terraform`+`GetHeightmapModifiers`+tunnel-pass |
| `RoadbedBuilder.BuildMasks` | `Track/RoadbedBuilder.cs:13` | `TrackSegment.Style` → modifier set per segment style |

---

## Spine: the modifier evaluation pipeline

```
[Authoring (per-machine)]
   StaticMapMask / MapMaskBase  (MonoBehaviour, OnEnable)
      └─ Builds an IMaskDescriptor (Curve/Rectangle/Simple)
      └─ Wraps it in HeightmapModifier / MaskModifier / TunnelModifier
      └─ MapManager.AddModifier(modifier, CoordinateSystem.Game|World)
            ├─ Apply -gameToWorldOffset if World-space
            ├─ Type-dispatch into _heightmapModifiers / _maskModifiers / _tunnelModifiers
            ├─ ModifierStorage<T>.Set(guid, modifier)  → AABB tree (Core.AABBTree<string>)
            └─ Invalidate(modifier.Mask.Bounds, 0.5f)
                  └─ _tilesPendingInvalidate += every tile intersecting bounds
                  └─ CoroutineTask InvalidateAfterDelayWorker(0.5f)
                        └─ each pending tile.Invalidated = true
                        └─ ScheduleWorkTilesIfNeeded → adds to _invalidatedTiles list

[Streaming (per-tile build)]
   MapManager.WorkLoadUnloadQueues
      ├─ overrides   (RequestPriorityLoad)
      ├─ invalidated (← modifier-driven rebuild)
      └─ queued      (camera-driven streaming)
   for each tile to build → BuildTerrain → TerrainBuilder.BuildTerrain(...)

[TerrainBuilder.BuildTerrain]
   1. ReadHeightTexture (PNG R/G → RFloat baseHeights)
   2. for each MaskName in { Object, CutTrees, Track, Water, Tree, Dirt }:
        ├─ GetHeightmapModifiers(maskName, list, out blendStyle)   ← MaskModifiers cast as HeightmapModifiers!
        ├─ blendStyle = (Water|Track|Object|Dirt → Max) | (Tree|CutTrees → Min)
        ├─ if no modifiers → CopyTexture(baseMask, ...)
        └─ else → HeightmapTextureSDFBuilder.CreateHeightmapTexture(fillCutMaterial, list, ...)
   3. Terraform (the actual heightmap mutation):
        ├─ Sort _heightmapModifiers by Order, group by Order
        ├─ for each group: HeightmapTextureSDFBuilder.CreateHeightmapTexture(..., heightmapModifiers, ...)
        └─ Blit final source → terrainData.heightmapTexture
   4. RenderSlopeMap (height → R8 slope texture, used by splat)
   5. for each TunnelModifier overlapping → TunnelHelper.ApplyTunnelModifier → terrainData.SetHoles(...)
   6. Splat (6× ApplySplat passes; consumes Track/Dirt/Tree masks + slope; writes alphamap)
   7. AddTrees (consumes Tree+Water+Object+Track masks via TreePlanter compute shader)
```

**The pipeline is single-pass-per-tile.** Modifiers are not evaluated globally; they're queried per-tile during build via `IMapModifierSource.HeightmapModifiersOverlapping(tilePosition)` etc. Each query hits the AABB tree once per tile. Every modifier whose `Mask.Bounds` intersects the 500m × 500m × 10000m tile box is included.

### Key constants

| Constant | Value | Site |
|---|---|---|
| Modifier `Invalidate` debounce | 0.5s realtime | `MapManager.AddModifier:1005`, `RemoveModifier:1013` |
| Max modifiers per heightmap blit | 256 | `HeightmapTextureSDFBuilder.cs:35` (logged "Too many curves" + truncated) |
| AABBTree node margin | 20f | `ModifierStorage.cs:11` (passed to `AABBTree<string>` ctor) |
| Tile bounds for overlap test | full tile rect × Y±5000m | `ModifierStorage.cs:45` (so any Y-axis modifier overlaps any tile in XZ) |
| Mask resolution | 256 (most) / 512 (`MaskName.Track` only) | `TerrainBuilder.cs:178` |
| `Hidden/Railroader/HeightToSlope` slope-map res | 512 | `TerrainBuilder.cs:384` |
| Tunnel hole half-width | 4f along curve right-vector | `TunnelHelper.cs:32-35` |
| Tunnel hole height range | `min(p1.y, p2.y) - 0.25` to `+ 8f` | `TunnelHelper.cs:41-42` |
| Tunnel chunk length per `ApplyTunnelModifier` call | 20m | `TunnelHelper.cs:24` |

---

## `IMapModifier` — the marker interface

```csharp
public interface IMapModifier {                                          // IMapModifier.cs
    IMaskDescriptor Mask { get; }
    IMapModifier OffsetBy(Vector3 offset);
}
```

Two members. `Mask` exposes the spatial extent (bounds + shape) for both the storage AABB index and the per-tile overlap query. `OffsetBy` is the floating-origin shim — `MapManager.AddModifier` calls it with `-_gameToWorldOffset` when `coordinateSystem == World` so storage is always game-space.

**There are exactly three concrete implementations.** `IMapModifier` is checked via `is`-pattern type-tests in `MapManager.AddModifier` (`MapManager.cs:986-994`); a fourth type would throw `ArgumentException("Unknown modifier type ...")` and would never reach the AABB tree. **Adding a custom modifier kind is not a Harmony surface — it requires either patching `AddModifier` or going through one of the three existing wrappers.**

---

## `HeightmapModifier` — terrain Y-axis modification

```csharp
public struct HeightmapModifier : IMapModifier {                         // HeightmapModifier.cs
    public HeightmapModifierKind Kind;     // BlendHeight | Roadbed | Min | Max
    public IMaskDescriptor       Mask { get; }
    public int                   Order { get; }                          // group key for staged blits

    public HeightmapModifier(int order, HeightmapModifierKind kind, IMaskDescriptor mask);
    public IMapModifier OffsetBy(Vector3 offset);
}
```

**Mutable struct** (note: `Kind` is a public field, not a property — re-assignable on a stored copy, but the storage hands out by-value via `IEnumerable<T>` so mutating a returned copy has no effect). `Order` is the staged-blit grouping key: `Terraform()` sorts heightmap modifiers by `Order`, groups by equal-`Order`, and runs one SDF-blit per group with the previous output as input. So `Order = 0` modifiers all blend together first, then `Order = 1` runs against that result, etc. `RoadbedBuilder` uses `Order = 1` for all track roadbed modifiers; `RiverPath.River` uses `Order = 0`; `RiverPath.Road` uses `Order = 1`. Same-`Order` modifiers blend additively within the SDF shader.

### `HeightmapModifierKind` semantics

| Kind | Effect | Used by |
|---|---|---|
| `BlendHeight` | Set absolute height to `Mask.Bounds.center.y` (interpreted via `SettingY`) | Switch-stand pads, scenery `enableSetHeight=true` |
| `Roadbed` | Lower terrain to a fixed offset below the curve (carves cut, builds fill) | Track + yard segments, `RiverPath.Road` |
| `Min` | Take min(existing, modifier-Y) — terrain only goes down | River carving (`MaskBlendStyle.Min` for Tree/CutTrees mask blend) |
| `Max` | Take max(existing, modifier-Y) — terrain only goes up | Inverse fill (Water/Track/Object/Dirt mask blend) |

The kind is encoded into the SDF shader as an int passed in `_Flags` slot 1 (`HeightmapTextureSDFBuilder.cs:84`: `num5 = (int)heightmapModifier.Kind`). Each modifier takes 4 `Vector4` slots in the shader's `_Curves` array (P0..P3 of the bezier or rect endpoints + thickness) and 2 `Vector4` slots in `_Flags` (kind/radius/falloff + endRadius/noise/scale).

### The `MaskModifier → HeightmapModifier` synthetic conversion

This is the **non-obvious part** of the pipeline. `TerrainBuilder.GetHeightmapModifiers` (`TerrainBuilder.cs:257`) turns each `MaskModifier` into a transient `HeightmapModifier` whose `Mask` is the `MaskModifier`'s mask **with its Y set to `value` (or `1f - value` for Min-blend)**. The mask-channel SDF blit then uses this list to render a 0..1 mask texture (per `MaskName`). So: a `MaskModifier(MaskName.Water, 1f, descriptor)` doesn't directly write `1.0` everywhere inside `descriptor`; it makes a synthetic `HeightmapModifier(0, Max, descriptor.SettingY(1f))`, which the SDF shader rasterizes as a smooth-falloff mask. The `Order` is hard-coded `0` for all synthetic mask-modifier converters (they don't inherit from the source `MaskModifier`).

### Patch candidates (HeightmapModifier)

| Method | Why patch |
|---|---|
| `MapManager.AddModifier` (prefix) | Inspect or veto every modifier registration. Useful for "blacklist this scenery from cutting trees." |
| `TerrainBuilder.Terraform` | The actual terraform pass. Patch postfix to read out the final RFloat texture before it's blitted to `terrainData.heightmapTexture`. |
| `HeightmapTextureSDFBuilder.CreateHeightmapTexture` | Replace the SDF math entirely. The `fillCutMaterial` is a `[SerializeField]` on `MapManager` so you can also swap the material. |
| `MapManager.HeightmapModifiersOverlapping` (postfix) | Inject extra modifiers per-tile dynamically without going through `AddModifier`. **No re-invalidate** — tile must already be queued. |

---

## `MaskModifier` — vegetation/water/track/dirt mask channels

```csharp
public readonly struct MaskModifier : IMapModifier {                     // MaskModifier.cs
    public readonly MaskName MaskName;     // which of 6 channels
    public readonly float    Value;        // 0..1 (typically 1f everywhere in vanilla)
    public IMaskDescriptor   Mask { get; }

    public MaskModifier(MaskName maskName, float value, IMaskDescriptor mask);
    public IMapModifier OffsetBy(Vector3 offset);
}
```

**Readonly struct** (immutable). `Value` is the contribution to the chosen channel; vanilla almost universally uses `1f` (the only divergence is `RoadbedBuilder.Standard` which uses `0.75` in the mask radius for the Track channel — but actually that's the `radius` arg, not the value; `Value` is `1f` everywhere in `RoadbedBuilder`).

### `MaskName` enum (the six channels)

| Channel | Source on disk | Blend style | Consumed by |
|---|---|---|---|
| `Track` | none (synthetic only) | Max | `MaskToSplat` shader → splat alphamap; `TreePlanter`/`DetailPlanter` to suppress vegetation on track |
| `Water` | `TileMaskName.Water` (PNG alpha bit 7) | Max | `TreePlanter`/`DetailPlanter` (no trees in water); not splatted as a channel directly |
| `Tree` | `TileMaskName.Vegetation` (PNG alpha bits 4-6, 4-bit) | Min | `TreePlanter` (per-biome density), `MaskToSplat`'s `_TreeMask` |
| `CutTrees` | none (synthetic only) | Min | `TreePlanter` to subtract trees (under bridges, telegraph poles, switches) |
| `Object` | none (synthetic only) | Max | Vegetation suppression (no trees in scenery footprints) |
| `Dirt` | none (synthetic only) | Max | `MaskToSplat`'s `_DirtMask` (yard splat) |

**`Track`, `CutTrees`, `Object`, `Dirt` have no on-disk source** — they exist only as synthetic textures built per-tile from registered `MaskModifier`s. If no modifiers register for a channel, `TerrainBuilder` falls back to `Texture2D.blackTexture` (Min-blend) or `Texture2D.whiteTexture` (Max-blend) to short-circuit the SDF blit (`TerrainBuilder.cs:174-185`). **`Water` and `Tree` are loaded from the PNG** and then *combined* with synthetic modifiers via the SDF blit if any are registered.

### Blend-style table

```csharp
blendStyle = maskName switch {                                            // TerrainBuilder.cs:262
    MaskName.Water    => MaskBlendStyle.Max,
    MaskName.Track    => MaskBlendStyle.Max,
    MaskName.Object   => MaskBlendStyle.Max,
    MaskName.Dirt     => MaskBlendStyle.Max,
    _                 => MaskBlendStyle.Min,    // Tree, CutTrees
};
```

`Max` channels grow with each modifier (paint more on top); `Min` channels shrink (CutTrees subtracts vegetation). The `Value` param plays opposite roles: for `Max` the synthetic HeightmapModifier sets Y=`Value` (1f → full); for `Min` it sets Y=`1f - Value` (1f → 0f, i.e., fully cleared).

### Adding a custom mask channel

The `MaskName` enum is **closed**. Adding a 7th value via Harmony postfix on `Enum.GetValues` won't work — `TerrainBuilder.cs:156-164` hard-codes the 6-element `MaskName[]` array, the splat shader has fixed `_TrackMask`/`_DirtMask`/`_TreeMask` uniforms, and tree planter compute shaders bind specific texture names. Custom mask channels require: (a) extending the `MaskName` enum (Harmony cannot extend enums; recompile required), or (b) repurposing an unused channel like `Object` or `Dirt`, or (c) running a parallel modifier system that consumes the same `MaskComponentBase` lifecycle but writes to mod-side textures not committed to vanilla `TerrainData`.

### Patch candidates (MaskModifier)

| Method | Why patch |
|---|---|
| `TerrainBuilder.GetHeightmapModifiers` | Filter or augment the per-channel modifier list per tile. Where you'd inject "no track masks for hidden tracks." |
| `TerrainBuilder.BuildMaskTexture` (private) | Rewrite the mask SDF blit. Also where to swap `Texture2D.blackTexture`/`whiteTexture` defaults. |
| `MapManager.MaskModifiersOverlapping` (postfix) | Yield extra mod-side mask modifiers dynamically. |

---

## `TunnelModifier` — terrain hole carving

```csharp
public readonly struct TunnelModifier : IMapModifier {                   // TunnelModifier.cs
    public IMaskDescriptor          Mask => CurveMaskDescriptor;          // alias
    public CurveMaskDescriptor      CurveMaskDescriptor { get; }

    public TunnelModifier(CurveMaskDescriptor maskDescriptor);
    public IMapModifier OffsetBy(Vector3 offset);
}
```

**Curve-only.** Unlike `HeightmapModifier`/`MaskModifier`, `TunnelModifier` is hard-bound to `CurveMaskDescriptor`. `RectangleMaskDescriptor` and `SimpleMaskDescriptor` cannot be used as tunnel sources; `TunnelHelper.ApplyTunnelModifier` reads `CurveMaskDescriptor.Curve` directly (`TunnelHelper.cs:20`) without checking `IMaskDescriptor` polymorphically.

### `TunnelHelper.ApplyTunnelModifier` algorithm

`TunnelHelper.cs:9-13`: called twice per tunnel, once for each end (`direction: true` and `false`). Each call:

1. Walks 20m along the bezier from one endpoint.
2. Computes a slicing plane at the endpoint's reverse-forward direction.
3. Expands `lowerBounds`/`upperBounds` by ±4m perpendicular to curve at both ends.
4. **Rejects (logs warning, returns) if the AABB exceeds tile resolution** (`TunnelHelper.cs:36-39`). A tunnel that crosses tile boundaries is silently incomplete — the portion outside the rejecting tile won't get holes.
5. For every terrain heightmap cell inside the AABB: hole=true if the cell is more than 4m off the curve right-axis, OR on the wrong side of the slicing plane, OR more than 8m above the lowest endpoint, OR below `min(p1.y, p2.y) - 0.25`.
6. `terrainData.SetHoles(lowerBounds.x, lowerBounds.y, holesArray)`.

**Sets `MapTerrain.HasHoles = true`** (`TerrainBuilder.cs:371`). This flag persists across re-builds and is referenced in the seam-fixing/save logic.

### Tunnel ↔ Track interaction (TrackSegment.Style.Tunnel)

`RoadbedBuilder.BuildMasks` at `case TrackSegment.Style.Tunnel:` (`RoadbedBuilder.cs:37`):

```csharp
staticMapMask.AddModifier(new TunnelModifier(new CurveMaskDescriptor(curve, 1.6f, 0f)));
float num = curve.CalculateLength();
float num2 = Mathf.Min(6f, num / 2f);
curve.Split(curve.ParameterForDistance(num2, 0.1f), out var l, out var r);
curve.Split(curve.ParameterForDistance(num - num2, 0.1f), out r, out var r2);
staticMapMask.AddModifier(new MaskModifier(MaskName.Track, 1f, new CurveMaskDescriptor(l, 4f, 6f)));
staticMapMask.AddModifier(new MaskModifier(MaskName.Track, 1f, new CurveMaskDescriptor(r2, 4f, 6f)));
```

So tunnel segments get: (a) one `TunnelModifier` for the whole curve, (b) two `MaskModifier(Track)` for the first/last 6m (or half-length if shorter) — the portal approaches. **No roadbed `HeightmapModifier`** for tunnel segments — the terrain inside a tunnel is left alone, only holes are punched.

### Patch candidates (TunnelModifier)

| Method | Why patch |
|---|---|
| `TunnelHelper.ApplyTunnelModifier` | Modify hole geometry (e.g., wider, taller, asymmetric). The hard-coded ±4m / +8m / -0.25m constants are pixel-perfect targets here. |
| `RoadbedBuilder.BuildMasks` `case Tunnel` | Replace the entire tunnel-segment authoring (e.g., add `Object` mask suppression around portals, custom portal scenery). |

---

## `IMapModifierSource` — the interface

```csharp
public interface IMapModifierSource {                                    // IMapModifierSource.cs
    IEnumerable<HeightmapModifier> HeightmapModifiersOverlapping(Vector2Int tilePosition);
    IEnumerable<MaskModifier>      MaskModifiersOverlapping(Vector2Int tilePosition);
    List<TunnelModifier>           TunnelModifiersOverlapping(Vector2Int tilePosition);
}
```

**`MapManager` is the only implementer.** `TerrainBuilder` takes an `IMapModifierSource` in its constructor (`TerrainBuilder.cs:91-94`) but is *always* passed `_mapManager` itself. The interface exists for testability/swap-out, not used as such in vanilla. Inconsistent return types: heightmap and mask return `IEnumerable<T>` (lazy via `ModifierStorage.Overlapping`), tunnel returns `List<T>` (eager `.ToList()` at `MapManager.cs:1032`).

**Mod recipe to inject extra modifiers**: implement `IMapModifierSource`, replace `MapManager`'s `_terrainBuilder` field via reflection or patch its constructor to wrap the original source. Do not patch `MapManager` itself to also implement a *second* source — there's no aggregation.

---

## `MapManager` — modifier registration & query

```csharp
public string AddModifier(IMapModifier modifier, CoordinateSystem coordinateSystem)   // 978
{
    string text = Guid.NewGuid().ToString();
    if (coordinateSystem == CoordinateSystem.World)
        modifier = modifier.OffsetBy(-_gameToWorldOffset);
    Bounds bounds = modifier.Mask.Bounds;
    if (modifier is MaskModifier m)         _maskModifiers.Set(text, m);
    else if (modifier is HeightmapModifier h) _heightmapModifiers.Set(text, h);
    else if (modifier is TunnelModifier t)    _tunnelModifiers.Set(text, t);
    else throw new ArgumentException($"Unknown modifier type {modifier}", "modifier");
    Invalidate(bounds, 0.5f);
    return text;
}

public void RemoveModifier(string modifierKey)                                          // 1009
{
    if (_heightmapModifiers.Remove(modifierKey, out var bounds)
     || _maskModifiers.Remove(modifierKey, out bounds)
     || _tunnelModifiers.Remove(modifierKey, out bounds))
        Invalidate(bounds, 0.5f);
}
```

`CoordinateSystem` enum (`MapManager.cs:54`):

| Value | Meaning |
|---|---|
| `CoordinateSystem.World` | Modifier coords are in world-space; will be offset by `-_gameToWorldOffset` to game-space |
| `CoordinateSystem.Game` | Modifier coords are already in game-space; passed straight through |

**`MaskComponentBase.AddModifier` always uses `CoordinateSystem.World`** (`MaskComponentBase.cs:50`). Code-driven sites (`StaticMapMask` consumers — `RoadbedBuilder`, `TrackObjectBuilder.CreateSwitchMasks`) explicitly set `CoordinateSystem = MapManager.CoordinateSystem.Game` because they're already operating in game-space. Mods that create a `StaticMapMask` should set `CoordinateSystem` explicitly before calling `AddModifier` — the default-zero of the enum is `World`.

### Storage: `ModifierStorage<T>`

```csharp
internal class ModifierStorage<T> where T : IMapModifier {                // ModifierStorage.cs
    private readonly Dictionary<string, T> _modifiers = new();
    private readonly AABBTree<string>      _tree      = new(20f);         // Core.AABBTree

    public void Clear();
    public void Set(string key, T modifier);             // upsert into tree+dict
    public bool Remove(string key, out Bounds bounds);   // returns dropped bounds
    public IEnumerable<T> Overlapping(Rect rect);        // tile XZ rect, Y±5000m fan
}
```

- `AABBTree` from `Core` namespace; `20f` is the inflation margin. **Updates are idempotent** — `Set` with the same key overwrites the entry without affecting the tree's structure (`AABBTree.Update`).
- `Overlapping(Rect)` uses the rect's XZ as a 10000m-Y-tall box for intersection test (`ModifierStorage.cs:45`). So Y-axis bounds on the modifier are always satisfied as long as XZ overlaps.
- `_tree.KeysIn(rect, hashSet)` is the broad-phase; the per-modifier `Bounds.Intersects` is the narrow-phase. The narrow-phase uses *the same* 10000m-Y box, so Y is effectively ignored.

### MapManager `Awake/OnEnable/OnDisable` and modifier persistence

`OnDisable` calls `RemoveAllTerrains()` which destroys terrain GOs but **does not clear `_heightmapModifiers`/`_maskModifiers`/`_tunnelModifiers`**. Modifiers persist across map enable/disable; only the cached terrain rebuilds. `RebuildAll()` (`MapManager.cs:210`) calls `ClearCaches() → RemoveAllTerrains()` → `LoadOrCreateStore()`; modifiers also survive this. The only path that clears modifier storage is `ModifierStorage.Clear()` — and **nothing in vanilla calls it.** Modifiers leak across scene loads in theory; in practice scene unload destroys the `MapManager` GameObject (it's in the map scene), so the whole `MapManager._instance` reference is invalidated. New scene → new `MapManager` → new empty storages.

**The leak vector**: if a mod holds a long-lived modifier GUID across scene unloads and tries to `RemoveModifier` after the second scene loads, it removes a modifier from the *new* `MapManager`'s empty storage (silent no-op via `if-out-bounds` short-circuit). The original modifier (in the now-destroyed old `MapManager`) is also gone. So GUID-based mod state must be cleared on `MapWillUnloadEvent`.

### Patch candidates (MapManager)

| Method | Why patch |
|---|---|
| `AddModifier` prefix | Veto/log every registration. Lets you intercept third-party mods' modifiers. |
| `AddModifier` postfix | Track the GUID for your own removal lifecycle. Useful for "rebuild-everything" debug commands. |
| `RemoveModifier` postfix | Mirror modifier-removal events to mod-side state. |
| `RebuildAll` | Inject mod-side modifier rebuilds (mods that compute modifiers asynchronously can re-register here). |
| `_heightmapModifiers`/`_maskModifiers`/`_tunnelModifiers` (private fields) | Direct reflection access to inspect/clear. The `ModifierStorage<T>` types are `internal`, so reflection requires non-public binding flags. |

---

## `MaskComponentBase` — the MonoBehaviour wrapper

```csharp
public abstract class MaskComponentBase : MonoBehaviour {                 // MaskComponentBase.cs:6
    private readonly HashSet<string> _modifierKeys = new();

    private void OnEnable()    { ApplyModifiers(); }
    private void OnDisable()   { RemoveModifiers(); }
    private void OnValidate()  { if (isActiveAndEnabled && MapManager.Instance != null) ApplyModifiers(); }
    public  void Rebuild()     { ApplyModifiers(); }

    protected virtual void ApplyModifiers() { RemoveModifiers(); }   // base clears, derived re-adds
    protected void AddModifier(IMapModifier modifier);               // → MapManager.AddModifier(.., World)
    private void RemoveModifiers();                                   // → MapManager.RemoveModifier per key
}
```

**Key facts:**

- `OnEnable` re-applies modifiers; `OnDisable` removes them. Disabling a `MaskComponentBase` GO removes its modifiers and triggers tile invalidation/rebuild.
- `OnValidate` re-applies on inspector edit (editor-only refresh). **`Rebuild()` is the public re-apply hook** — call after mutating component fields at runtime.
- Always uses `CoordinateSystem.World` for `AddModifier` (`MaskComponentBase.cs:50`). Subclasses cannot override this.
- The `RemoveModifiers` private method silently no-ops if `MapManager.Instance == null` — useful during scene unload but means the `_modifierKeys` set may leak entries if a mod removes the `MapManager` while the component is alive.
- `ApplyModifiers` calls `base.ApplyModifiers()` first (which clears the set), then derived classes call `AddModifier` to re-populate. **If a subclass forgets `base.ApplyModifiers()`, modifiers double-up on every call.**

### `MapMaskBase` — the editor-authored shape MonoBehaviours

```csharp
public abstract class MapMaskBase : MaskComponentBase {                   // MapMaskBase.cs:8
    [Range(0,50)] public float radius = 10f;
    [Range(0,50)] public float falloff = 10f;
    public bool   enableSetHeight;
    public bool   enableCutTrees;            // formerly "enableVegetationMask"
    public bool   enableMaskModifier;
    public MaskName maskName = MaskName.Object;
    [Range(-5,5)] public int order;

    protected abstract IMaskDescriptor MakeMaskDescriptor();

    protected override void ApplyModifiers() {
        base.ApplyModifiers();
        var mask = MakeMaskDescriptor();
        if (enableSetHeight)    AddModifier(new HeightmapModifier(order, HeightmapModifierKind.BlendHeight, mask));
        if (enableCutTrees)     AddModifier(new MaskModifier(MaskName.CutTrees, 1f, mask));
        if (enableMaskModifier) AddModifier(new MaskModifier(maskName, 1f, mask));
    }
}
```

Three independent toggle-flags multiply the same mask descriptor into up to three modifiers per component. **`enableSetHeight` always uses `HeightmapModifierKind.BlendHeight`** — there's no UI for `Roadbed`/`Min`/`Max` from the inspector. **`enableCutTrees` always writes `MaskName.CutTrees`** with value `1f` regardless of `maskName`. The `maskName` field only controls the third `enableMaskModifier`-gated channel.

#### Subclasses

| Class | Mask descriptor | Notes |
|---|---|---|
| `CircleMapMask` | `CurveMaskDescriptor` of degenerate near-zero-length curve at component position | Used for point-shaped masks (e.g., switch stand pads in scenery defs) |
| `RectangleMapMask` | `RectangleMaskDescriptor(transform.position, (sizeX, 0, sizeZ), rotation+degrees, radius, falloff)` | Used for industry footprints, building pads |
| `CurveMapMask` | `CurveMaskDescriptor` between two endpoint+rotation pairs | Used for serpentine paths |
| `RiverPath` | Multi-segment Bezier walking `points[]` list, emits one HeightmapModifier + one MaskModifier per segment, plus an extra Dirt mask for `Road` style | River vs Road style chosen via inline enum |

`RiverPath` is special — it's a `MaskComponentBase` directly (not via `MapMaskBase`) and emits its own modifier triplets per segment. `RiverPath.River` style uses `HeightmapModifierKind.Min` (carves a riverbed) and `MaskName.Water`; `RiverPath.Road` uses `Roadbed` kind and `MaskName.Object` + `Dirt`.

### `StaticMapMask` — code-driven sites

```csharp
[ExecuteInEditMode]
public class StaticMapMask : MonoBehaviour {                              // StaticMapMask.cs:9
    [NonSerialized] public MapManager.CoordinateSystem CoordinateSystem;  // default World (=0)!
    private bool _enabled;
    private readonly HashSet<string> _addedKeys = new();
    private readonly HashSet<IMapModifier> _pending = new();

    private void OnEnable()  { _enabled = true; AddPending(); }
    private void OnDisable() { _enabled = false; RemoveModifiers(); }

    public void AddModifier(IMapModifier modifier);    // batch-buffer if not enabled, else immediate
    public void RemoveModifiers();                     // clear, with null-MapManager-safe path
    private void AddPending();                         // flush buffer
}
```

**Distinct from `MaskComponentBase`** — uses its own `_addedKeys` set, has a `_pending` buffer for modifiers added before `OnEnable`, and exposes `CoordinateSystem` as a public field. **`CoordinateSystem` defaults to `World` (enum default 0)** — code-driven sites *must* explicitly set it to `Game` before calling `AddModifier`. Both `RoadbedBuilder.BuildMasks` (`RoadbedBuilder.cs:16`) and `TrackObjectBuilder.CreateSwitchMasks` (`TrackObjectBuilder.cs:109`) and `TelegraphPoleManager.RebuildMapMasks` (implied via Game-space inputs) follow this pattern.

#### `StaticMapMask` buffering quirk

If `AddModifier` is called before `OnEnable` runs, the modifier sits in `_pending` until enable-time. **There's no upper bound on `_pending`.** A long-disabled `StaticMapMask` can accumulate hundreds of modifiers and dump them all on enable. Useful for offline-authoring; dangerous for runtime hot-loops.

#### Patch candidates (mask MonoBehaviours)

| Method | Why patch |
|---|---|
| `MaskComponentBase.ApplyModifiers` | Insert mod-side modifiers per component (e.g., add a `Tree` modifier wherever a circle mask exists). |
| `StaticMapMask.AddModifier` | Track all programmatically-added modifiers (pre/post-enable). |
| `MapMaskBase` subclasses' `MakeMaskDescriptor` | Override mask shape per scenery type. |

---

## Mask descriptors (`IMaskDescriptor` family)

```csharp
public interface IMaskDescriptor {                                        // IMaskDescriptor.cs
    Bounds Bounds { get; }
    IMaskDescriptor SettingY(float y);     // returns new with center.y = y
    IMaskDescriptor OffsetBy(Vector3 offset);
}
```

Three implementations:

### `RectangleMaskDescriptor` (struct)

```csharp
public readonly struct RectangleMaskDescriptor : IMaskDescriptor {        // RectangleMaskDescriptor.cs
    internal readonly Vector3 EndpointA, EndpointB;
    internal readonly float   Thickness;          // half-X
    public float Radius { get; }
    public float Falloff { get; }
    public Bounds Bounds { get; }

    // Two constructors:
    //   (a, b, thickness, radius, falloff, bounds)   ← internal direct
    //   (center, size, yRotationDegrees, radius, falloff)   ← public, builds a/b from rotation
}
```

The **public constructor swaps X/Z if `size.x > size.z`** (`RectangleMaskDescriptor.cs:31-40`) — adds 90° to the rotation. This is to ensure `Thickness` is always derived from the smaller dimension. `Bounds` is `(center, size * 2f)` — **note the 2× expansion**, accounting for the radius/falloff extension.

Used as the modifier mask for: switch stand pads, telegraph pole footprints, scenery rectangles (industry buildings, etc.).

### `CurveMaskDescriptor` (struct)

```csharp
public readonly struct CurveMaskDescriptor : IMaskDescriptor {            // CurveMaskDescriptor.cs
    public readonly BezierCurve Curve;             // Core.BezierCurve
    public float Radius            { get; }
    public float Falloff           { get; }
    public float BeginRadiusFactor { get; }
    public float EndRadiusFactor   { get; }
    public float RadiusNoise       { get; }
    public float NoiseScale        { get; }
    public Bounds Bounds           { get; }

    public CurveMaskDescriptor(BezierCurve curve, float radius, float falloff, float radiusNoise=0, float noiseScale=1);
    public CurveMaskDescriptor(BezierCurve curve, float beginRadiusFactor, float endRadiusFactor, float radius, float falloff, float radiusNoise, float noiseScale);
}
```

The 4-argument convenience ctor sets begin/end factor to `1f`. Used by all track-segment modifiers (the bezier is the segment's roadbed curve), telegraph wire arcs, river segments. **Tunnels are curve-only** — `TunnelModifier(CurveMaskDescriptor)` is the only valid construction.

`Bounds` is computed via `BoundsExtensions.GetBounds(curve, radius * Mathf.Max(beginRadiusFactor, endRadiusFactor) + falloff)` — i.e., the bezier's own bounding box inflated by `radius+falloff`. This drives AABBTree intersection tests.

### `SimpleMaskDescriptor` (struct)

```csharp
public readonly struct SimpleMaskDescriptor : IMaskDescriptor {           // SimpleMaskDescriptor.cs
    public Bounds Bounds { get; }                  // bare AABB

    public SimpleMaskDescriptor(Vector3 center, Vector3 size);
}
```

**No vanilla call sites** in the searched paths. Reserved for mod use? `HeightmapTextureSDFBuilder.CreateHeightmapTexture` only handles `CurveMaskDescriptor` and `RectangleMaskDescriptor` (`:60-75`); a `SimpleMaskDescriptor` passed as a `HeightmapModifier` mask is **silently skipped** by the `continue` at `:64`. So `SimpleMaskDescriptor` only works for AABB-tree placement (via `Bounds`); it has no rendering effect. Dead-end type; do not use as a modifier mask in vanilla.

### Patch candidates (descriptors)

| Type | Why patch |
|---|---|
| `RectangleMaskDescriptor` ctor | Add custom rotation logic, override `Bounds` expansion. Struct can't be subclassed. |
| `CurveMaskDescriptor` ctor / `Bounds` getter | Tweak intersection inflation, custom noise channels. |
| `HeightmapTextureSDFBuilder.CreateHeightmapTexture` | Add a third descriptor branch (e.g., custom polygon descriptor). |

---

## Track.Segment ↔ modifier integration

The chokepoint is `RoadbedBuilder.BuildMasks(BezierCurve, GameObject parent, TrackSegment.Style, string key)`:

```csharp
public static void BuildMasks(BezierCurve curve, GameObject parent, TrackSegment.Style style, string key)
{
    StaticMapMask staticMapMask = parent.AddComponent<StaticMapMask>();
    staticMapMask.CoordinateSystem = MapManager.CoordinateSystem.Game;
    switch (style) {
        case TrackSegment.Style.Standard: { ... }   // Roadbed cut + Track mask
        case TrackSegment.Style.Yard:     { ... }   // Wider Roadbed + Object + Dirt masks
        case TrackSegment.Style.Bridge:   { ... }   // CutTrees mask only (no roadbed!)
        case TrackSegment.Style.Tunnel:   { ... }   // TunnelModifier + portal Track masks
        default: throw new ArgumentOutOfRangeException(...);
    }
}
```

**Per-segment table (modifiers emitted per `TrackSegment.Style`):**

| Style | HeightmapModifier (Roadbed) | MaskModifier(Track) | MaskModifier(Object) | MaskModifier(Dirt) | MaskModifier(CutTrees) | TunnelModifier |
|---|---|---|---|---|---|---|
| Standard | curve + (0,-0.2,0), r=0.25, f=20 | curve, br=0.25, er=8, r=0.75, f=1.5 | — | — | — | — |
| Yard | curve + (0,-0.2,0), r=1.5, f=20 | — | curve, r=4, f=4 | curve, r=2, f=6 | — | — |
| Bridge | — | — | — | — | curve, r=6, f=14 | — |
| Tunnel | — | first/last 6m, r=4, f=6 | — | — | — | full curve, r=1.6, f=0 |

**Bridge segments do NOT cut a roadbed.** They only suppress vegetation. The bridge mesh itself is rendered as a separate scenery object. **Tunnel segments do not raise/lower terrain** — they only carve holes (via `TunnelModifier`) and suppress vegetation at portals (via two `MaskModifier(Track)` chunks).

**All segment modifiers are GameObject-anchored.** When the segment's GameObject is destroyed (track removal, scene unload), the `StaticMapMask`'s `OnDisable` fires `RemoveModifiers`, which `MapManager.RemoveModifier`s each GUID, which `Invalidate(bounds, 0.5f)`s — so the next 0.5s coalesces all per-segment removals into one batch invalidate.

### `TrackObjectBuilder.CreateSwitchMasks`

`TrackObjectBuilder.cs:96-114`. Switch-stand pads get a single `HeightmapModifier(1, BlendHeight, RectangleMaskDescriptor(0.1×0.1×0.1, r=0.75, f=2))` to flatten the ground under the stand. The switch's two arms each get a `RoadbedBuilder.BuildMasks` call too.

### `TrackObjectBuilder.CreateBumperMasks`

Just calls `RoadbedBuilder.BuildMasks` with a 1.25m straight bezier. No additional modifiers.

### Patch candidates (track integration)

| Method | Why patch |
|---|---|
| `RoadbedBuilder.BuildMasks` | Single chokepoint for per-track-style modifier authoring. Postfix to add custom per-style modifiers. |
| `TrackObjectBuilder.CreateSwitchMasks` | Customize switch stand pad geometry. |
| `TrackObjectBuilder.CreateBumperMasks` | Customize bumper modifier (extends the curve straight line to a raised mound, etc.). |

---

## Other modifier sources

### Telegraph poles

`TelegraphPoles/TelegraphPoleManager.cs:340`: `RebuildMapMasks()` adds a `StaticMapMask` and emits one `MaskModifier(CutTrees, 1f, 1m × 3m radius rect)` per pole node, plus one per inter-node edge wrapping the pole-to-pole span. So telegraph pole rights-of-way are kept tree-clear automatically.

### Industries / Scenery (`BaseMapMaskComponent` family)

`Definition/Model.Definition.Components.MapMasks/`:

```csharp
public abstract class BaseMapMaskComponent : Component {                  // BaseMapMaskComponent.cs:3
    public int   Order        { get; set; } = 1;
    public float Radius       { get; set; }
    public float Falloff      { get; set; }
    public bool  EnableObjectMask  { get; set; }
    public bool  EnableSetHeight   { get; set; }
    public bool  EnableCutTrees    { get; set; }
}

[Component(ComponentDefinitionMask.Scenery, ComponentLifetime.Static)]
public class CircleMapMaskComponent    : BaseMapMaskComponent { /* Kind = "CircleMapMask" */ }
public class RectangleMapMaskComponent : BaseMapMaskComponent {
    public Vector2 Size { get; set; } = (1,1);   // Kind = "RectangleMapMask"
}

[HideInEditor, Component(...)]
public class LegacyMapMaskComponent : Component {                         // LegacyMapMaskComponent.cs
    public int   Order, DimensionA, DimensionB, Radius, Falloff;          // legacy Sketchfab-era format
}
```

`MapMaskComponentBuilder` (`Model.ComponentBuilders/MapMaskComponentBuilder.cs:14`) bridges definition→runtime:

```csharp
private void _Build(ComponentBuilderContext ctx, BaseMapMaskComponent component) {
    MapMaskBase mb;
    if (component is CircleMapMaskComponent)         mb = ctx.GameObject.AddComponent<CircleMapMask>();
    else if (component is RectangleMapMaskComponent rect) {
        var r = ctx.GameObject.AddComponent<RectangleMapMask>();
        r.sizeX = rect.Size.x; r.sizeZ = rect.Size.y;
        mb = r;
    }
    else throw new ArgumentException("Unexpected component type");
    mb.order = component.Order;
    mb.radius = component.Radius;
    mb.falloff = component.Falloff;
    mb.enableMaskModifier = component.EnableObjectMask;
    mb.maskName = MaskName.Object;          // ← always Object; can't customize via JSON
    mb.enableSetHeight = component.EnableSetHeight;
    mb.enableCutTrees = component.EnableCutTrees;
}
```

**The JSON-defined `BaseMapMaskComponent` cannot pick a `MaskName` other than `Object`.** That's a hard-coded assignment. Mods that want a JSON-defined `Tree` or `Dirt` mask must:
- Define a custom `Component` subclass + `IComponentBuilder`, or
- Replace the runtime `MaskName.Object` assignment via Harmony postfix on `MapMaskComponentBuilder._Build`.

`LegacyMapMaskComponentBuilder` is the back-compat path for older scenery definitions; it's `[HideInEditor]` and always emits a `RectangleMapMask` with both `enableMaskModifier=true` and `enableSetHeight=true`.

### Definition-time scenery

The scenery placement system (industries, props) attaches a `RectangleMapMask` or `CircleMapMask` MonoBehaviour at scene-build time via the `IComponentBuilder` pipeline. These are `OnEnable`-driven — their modifiers register the moment the scenery GameObject becomes active, and re-register whenever it toggles enabled/disabled. **MP-divergence vector**: if scenery is enabled on the host but not on a client (e.g., progression-gated content), the client's terrain won't reflect the host's modifiers.

### Cars / Locomotives

**Cars do not register modifiers.** `BaseMapMaskComponent` is `[Component(ComponentDefinitionMask.Scenery, ComponentLifetime.Static)]` — only available to scenery-class definitions, not car definitions. Car GOs never carry `MaskComponentBase` MonoBehaviours.

### Patch candidates (other sources)

| Method | Why patch |
|---|---|
| `MapMaskComponentBuilder._Build` | The chokepoint to customize JSON-driven scenery modifier authoring. Postfix to override `maskName` per scenery prefab. |
| `LegacyMapMaskComponentBuilder._Build` | Same for legacy scenery format (rare to need; kept for back-compat). |
| `TelegraphPoleManager.RebuildMapMasks` | Customize telegraph rights-of-way clearance (e.g., wider, asymmetric). |

---

## Tile interaction: when modifiers run, what they output

### Trigger surface

| Trigger | Where | What happens |
|---|---|---|
| Tile *first build* (camera streams in) | `MapManager.WorkLoadUnloadQueues → BuildTerrain` | All overlapping modifiers consulted in `TerrainBuilder.BuildTerrain` |
| Tile *invalidated* (modifier added/removed nearby) | `Invalidate(Bounds, 0.5f)` → coroutine → `Invalidated=true` → next `WorkLoadUnloadQueues` slice | Rebuild from scratch (no diff, no patch — full re-bake) |
| Tile *steal-recycled* under pool pressure | `PrepareTerrain:852` | Same as first build — `BuildTerrain` re-runs against current modifier set |
| Manual `MapManager.Invalidate(Vector2Int)` | `MakeSeamless`, `RebuildAll` paths | Synchronous flag set; next `ScheduleWorkTilesIfNeeded` slice rebuilds |
| `MapCameraUpdater.SetTerrainDensityValues` | Density preference change | Calls `RebuildAll` — full reset of all terrains and modifiers re-evaluate |

**No "modifier changed" event fires.** Modifier additions are silent; the only side-effect is the deferred `Invalidate(bounds, 0.5f)`. To observe modifier changes, patch `MapManager.AddModifier` or `RemoveModifier`.

### Output: per-tile artifacts

For each tile-build the modifier system contributes:

1. **Heightmap (RFloat texture)** — written to `terrainData.heightmapTexture` via `Graphics.Blit(source, temporary, HeightmapBlitMaterial())`. Modifier groups blit-iterated by `Order`.
2. **6 mask textures (RFloat, 256 or 512 res)** — Track/Object/Dirt/Tree/Water/CutTrees, used downstream by Splat + tree planting. Held in `context.MaskTextures` for the duration of the build, then returned to `_texturePools`.
3. **1 slope texture (R8, 512 res)** — derived from heightmap via `Hidden/Railroader/HeightToSlope`, consumed by Splat and detail planter.
4. **Splat alphamap** (4 channels × `alphamapTextureCount` textures) — written to `terrainData.GetAlphamapTexture(i)` via `Hidden/MicroVerse/RasterToTerrain`.
5. **Tree instances** — `terrain.AddTreeInstance` calls per planted tree, density-modulated by `treeDensity` setting.
6. **Detail layers** — `terrainData.SetDetailLayer(0,0, idx, map)` per detail prototype.
7. **Tunnel holes** — `terrainData.SetHoles(...)` per `TunnelModifier`.

### Time budget

`TerrainBuilder.Budget = new TimeBudget(5)` (5ms per slice). Yields between phases (BuildMasks, Terraform, Splat, Trees) and within the inner loops if budget elapses. So a single tile build can span multiple frames; the modifier list snapshot is captured at the start of `BuildTerrain`, not re-queried per frame.

**Race window**: a modifier added mid-tile-build will not affect the in-flight build. Its `Invalidate(0.5f)` call enqueues the same tile for re-build, which runs after the current build finishes.

---

## On-disk PNG codec & masks

(Cross-reference: [Tile Loading & Bardo › On-disk layout](tile-loading-bardo.md#on-disk-layout); the modifier system *consumes* the `Water` and `Vegetation` channels here.)

`TileData.Save` (`TileData.cs:186-238`) — pixel layout for `tile_xxx_yyy.data` PNG:

```
R = (heightUshort >> 8) & 0xFF
G = heightUshort & 0xFF                   // heightUshort = (height_meters - 500) * 65.535
B = 0  (always; reserved for mod-extension)
A = (Water << 7) | (Vegetation_4bit << 4)
```

Where:
- `Water` is 1 bit (`PackValue(byte, 1)` = round to 1-bit), packed at alpha bit 7.
- `Vegetation` is `255 - rawVegetation`, packed as 3 bits at alpha bits 4-6 (`PackValue(byte, 3)` = round to 3-bit). **Comment in tile-loading-bardo says 4 bits — actually 3 bits** in the codec (`PackValue(value2, 3)` + `<< 4` shift = bits 4,5,6).
- Alpha bits 0-3 are unused (always 0).

`TileData.LoadIfNeeded` runs `PopulateHeightmap` Burst job to decode the heightmap. **Masks (`TileMaskName.Water`, `TileMaskName.Vegetation`, `TileMaskName.BiomeControl`) are decoded lazily on `GetMask` call** via `TileDataUnpacker.Unpack` (`TileDataUnpacker.cs:8`). The `BiomeControl` mask is not in the PNG either — it's loaded from a separate texture or default. `TileDataUnpackJob` extracts the bitfield based on `MaskName`.

**`Resolution = 513` for heightmap, `512` for masks** (one less; PNGs are still 513×513 but mask cells are at the inner cell centers). The mask write loop in `Save()` uses `num = resolution - 1` and skips the last row/column (`TileData.cs:211-223`), matching the 512×512 mask size.

### B channel

**Always 0 on save, ignored on load.** A free 8-bit channel for mods. `TileData.PopulateHeightmap` reads only R+G; the B byte is in `ColorARGB32` but never indexed. Mods can:
- Patch `TileData.Save` to populate B with custom data.
- Patch `TileData.PopulateHeightmap.Execute` to read B and stash to a parallel `NativeArray`.
- Tile builds will preserve B if the file is loaded then re-saved (since the codec round-trips R+G+A and zeroes B; **wait — the codec ZEROES B on save**, so any prior B data is destroyed by `TileData.Save`. Use a separate file for mod data.)

---

## MP authority & divergence implications

(Cross-reference: [Tile Loading & Bardo › MP behaviour](tile-loading-bardo.md#mp-behaviour--per-machine-no-sync) — the broader "no MP for tiles" story.)

The modifier system has **zero multiplayer integration**. Every machine independently:

1. Loads the same map scene → same `MapMaskBase`/`StaticMapMask` MonoBehaviours instantiate via Unity scene loading.
2. Runs the same `OnEnable` → `AddModifier` calls.
3. Builds tiles independently as the local camera streams.

**As long as the same scene is loaded**, host and clients get identical modifier sets and identical tile bakes. Determinism comes from Unity scene serialization, not from any sync layer.

### Divergence vectors

| Cause | Effect | Detection |
|---|---|---|
| Host-only mod adds runtime modifier (not present on client) | Host tile rebuilds with modifier; client tile doesn't. **Permanent visual divergence** in that area. | None — silent. Manual inspection or dual-client comparison. |
| Client-only mod adds modifier | Client sees terrain mutation host doesn't | Same — silent. |
| `MaskComponentBase`-driven scenery enabled on host but disabled on client (e.g., progression-gated GameObject) | Client doesn't see the modifier (the MonoBehaviour is disabled), so client's terrain reflects un-modified base | Visual: missing roadbed/clearance under conditional scenery |
| Different `_gameToWorldOffset` per machine when modifier registered as `World`-coords | Host registered relative to host's offset; client relative to client's | `OffsetBy(-_gameToWorldOffset)` is per-machine. Mods using `CoordinateSystem.World` from a non-game-space code path might desync. |
| Steal-furthest pool pressure rebuilds tile with stale modifier list | None — modifier list is always queried fresh | Safe. |

### "How to make modifier additions MP-safe"

Vanilla offers no template. Possible mod approaches:

1. **Run identical code on every machine** (e.g., trigger from a deterministic signal like `Messenger.Default.Send<MapDidLoadEvent>`). Best for static content.
2. **Define your own request message** and broadcast modifier descriptors. Each peer locally re-creates the modifier from the wire format on receipt. Requires custom `IGameMessage` + handler. The modifier descriptors are not serializable out-of-the-box — `BezierCurve`, `Bounds`, `Vector3` are MessagePack-friendly but `IMaskDescriptor` is interface-typed.
3. **Use KVO with HostOnly auth** on a singleton mod object to publish "active modifier list," each peer re-syncs on KVO update. High-volume KVO writes are not what KVO is optimized for.

**No vanilla mod-extension hook exists for MP modifier sync.** Mods that need it must build it.

---

## Save/load behaviour

(Cross-reference: [Save/Load](save-load.md), [Tile Loading & Bardo › Save/load](tile-loading-bardo.md#save--load-interaction-with-tiles).)

**Modifiers are not in the save format.** `WorldStore`/`Snapshot` schemas don't include any `IMapModifier` records. `MapManager._heightmapModifiers`/`_maskModifiers`/`_tunnelModifiers` are runtime-only.

On `MapWillUnload` → `MapDidUnload`:
- Map scene unloads, every `MaskComponentBase`/`StaticMapMask` MonoBehaviour `OnDisable` fires.
- Each `OnDisable` calls `RemoveModifiers` which `MapManager.RemoveModifier`s every owned GUID.
- The `MapManager` GameObject itself destructs (it's in the map scene).

On next `MapWillLoad` → `MapDidLoad`:
- New scene loads, new `MapManager`, new empty modifier storages.
- `OnEnable` of each `MaskComponentBase`/`StaticMapMask` fires, re-registering modifiers.

**A mod that wants modifier persistence** must:
- Hook `MapWillUnloadEvent` to snapshot its modifier descriptors.
- Hook `MapDidLoadEvent` to re-register on the new `MapManager`.
- Re-create any custom `IMaskDescriptor` data from the snapshot.

---

## Patch points: cross-cutting

### "Add a custom modifier type"

Three sub-tasks:

1. **Define your modifier struct** implementing `IMapModifier`. Inherit `Mask`/`OffsetBy` semantics from one of the descriptor types.
2. **Patch `MapManager.AddModifier`** prefix to type-test for your modifier and store in your own `Dictionary<string, YourModifier>`. Return a GUID prefix that `RemoveModifier` can recognize.
3. **Patch `TerrainBuilder.BuildTerrain`** (or a lower-level method like `Terraform`) to query your storage and apply your modifier's effect to the tile build. You'll need to either:
   - Render into one of the existing 6 mask textures (cleanest, no new render-pass).
   - Add a 7th mask channel (requires patching the `MaskName[] array = { ... }` at `TerrainBuilder.cs:156-164` AND extending the splat shader).
   - Run a separate post-process pass on the heightmap.

**Cleanest pattern**: implement your mod's runtime as `IMapModifier`-implementing struct + `IMapModifierSource` aggregator that vanilla can query in addition to itself. Patch `TerrainBuilder` constructor to accept a composite source.

### "Add a custom mask channel"

Hard. Requires:
- Adding a value to the closed `MaskName` enum (Harmony cannot extend enums; patch the array literals at `TerrainBuilder.cs:156-164` and `:262-268` using a sentinel-int value if your mod defines a separate enum).
- Adding a sampler binding to `Hidden/Railroader/MaskToSplat` shader if you want it splatted, or to your own custom shader.
- Adding consumer logic somewhere downstream (e.g., a custom tree-planter).

**Easier alternative**: repurpose `MaskName.Dirt` or `MaskName.Object` if you don't use them. Both are Max-blend Y-channel mask textures with fixed shader bindings.

### "Mod-side terrain authoring"

Two recipes:

**Recipe A: code-driven (like `RoadbedBuilder`)**
```csharp
// On scene load (MapDidLoadEvent), per-mod-feature:
GameObject parent = new GameObject("MyMod_TerrainAuthoring");
StaticMapMask mask = parent.AddComponent<StaticMapMask>();
mask.CoordinateSystem = MapManager.CoordinateSystem.Game;
mask.AddModifier(new HeightmapModifier(1, HeightmapModifierKind.Roadbed,
    new CurveMaskDescriptor(myCurve, radius: 1f, falloff: 5f)));
parent.SetActive(true);  // OnEnable → flush pending → AddModifier on MapManager
// To remove later: Object.Destroy(parent) → OnDisable → RemoveModifiers
```

**Recipe B: editor-authored (like `RectangleMapMaskComponent`)**
- Define a JSON `Component` subclass + `IComponentBuilder`.
- Place via standard scenery placement.
- Lifecycle managed by Unity scene + Component pipeline.

### "Per-tile callback after modifier-driven rebuild"

No event. Patch `MapManager.WorkLoadUnloadQueues` postfix or `MapTerrain` properties. The `MapTerrain.buildStatus` becomes `Ready` when modifier-driven rebuild completes; poll or patch `BuildTerrain` postfix.

### "Listen for modifier-driven invalidation"

Patch `MapManager.Invalidate(Bounds, float)` (private, but accessible via reflection) or wrap `AddModifier`/`RemoveModifier` callers. The `Invalidate(Vector2Int)` public overload is also called from `MakeSeamless`.

---

## Race conditions & async pitfalls

- **`AddModifier` does not block on tile rebuild.** The modifier appears in storage immediately, but tile rebuild takes 0.5s (debounce) + queue-position + ~5–20ms build slice. Mods that add a modifier and then try to read terrain heights via `MapManager.FindTerrainPointForXZ` will see *pre-modifier* heights. Wait for `MapTerrain.buildStatus == Ready` after the debounce.
- **Multiple modifier registrations within 0.5s coalesce.** A mod that registers 100 modifiers in a single frame triggers ONE 0.5s-delayed batch invalidate. Good for performance; bad for ordering — none of the 100 can be rebuilt independently.
- **Coalesced invalidation is per-tile, not per-modifier.** Removing modifier A and adding modifier B within the same 0.5s window invalidates the union of their bounds; both are present in the storage when rebuild happens.
- **Tile build snapshots the modifier list at build start.** A modifier registered mid-build won't affect this build but will trigger another (race-prevent free; double-rebuild possible).
- **Steal-recycled tiles bypass the 0.5s debounce.** `PrepareTerrain` immediately rebuilds with current modifier set. Mods that stagger modifier changes per-tile may see asymmetric application.
- **`StaticMapMask._pending` accumulates without limit before `OnEnable`.** Disabling and re-enabling a `StaticMapMask` with hundreds of pending modifiers spikes one-shot.
- **`MapMaskBase.OnValidate` runs in editor on every inspector edit.** Calls `ApplyModifiers` which `RemoveModifiers` then re-`AddModifier`. Each pair triggers an `Invalidate(bounds, 0.5f)` — debouncing in place, but the 0.5s clock restarts on every keystroke. Bulk inspector edits can defer rebuild by minutes.
- **`HeightmapTextureSDFBuilder` truncates at 256 modifiers per blit.** Beyond 256 in a single `Order` group: log error, drop the rest. So `Order`-grouping is implicitly the way to bypass the cap (run multiple groups). Vanilla never approaches this.
- **Tunnel modifier is silent-skipped if the tunnel AABB exceeds the tile**. `TunnelHelper.cs:36-39`. Long tunnels crossing tile boundaries will have unholed sections at the boundary cells.
- **`SimpleMaskDescriptor` is silently skipped by `HeightmapTextureSDFBuilder`** (`:60-75` only handles Curve and Rectangle). A `HeightmapModifier(_, _, SimpleMaskDescriptor)` registers in storage and triggers tile invalidation but has zero rendering effect.
- **`MapManager` modifier storages don't `Clear()` on `RebuildAll`.** Surviving modifiers re-apply to the rebuilt tiles — usually intentional, but if your mod expects to re-register from scratch, call `RemoveModifier` first.
- **No exception path in `Invalidate` on `_store == null`.** `Invalidate(bounds, 0.5f)` early-returns silently if `_store` is null (i.e., `MapManager` exists but hasn't loaded its store yet). A modifier added before `LoadOrCreateStore` runs is in the AABB tree but no tile is invalidated. The first tile-build to touch its area will pick it up via `HeightmapModifiersOverlapping`, so it's not lost — just no explicit rebuild trigger.
- **`OnDisable` of `MaskComponentBase` runs during scene unload AFTER `MapManager` may have been destroyed.** `_modifierKeys.Count == 0` early-out gates it; if a previous removal failed (`MapManager.Instance == null` early-return), keys persist in `_modifierKeys` and the next `OnEnable` doesn't auto-clean them. Mods that enable+disable the same component repeatedly may leak keys in their tracker but the actual modifier in `MapManager` is gone with the destroyed GameObject.

---

## Cross-references

- [Tile Loading & Bardo › Modifier types](tile-loading-bardo.md#modifier-types) — the original brief mention of modifiers; this sheet is the deep dive.
- [Tile Loading & Bardo › Tile invalidation](tile-loading-bardo.md#tile-invalidation-rebuild-in-place) — how `Invalidate(Bounds, float)` slots into the streaming queue.
- [Track Topology](track-topology.md) — track segments are the dominant modifier source via `RoadbedBuilder.BuildMasks`.
- [Floating Origin › MapManager interaction](floating-origin.md#mapmanager-interaction--the-unloaded-tile--bardo-region) — `_gameToWorldOffset`, the per-machine origin, and how `OffsetBy` shims `World`-coord modifiers.
- [Map Mods Vanilla Survey](../map-mods-vanilla-survey.md) — companion narrative; modifiers are not part of map *content* (PNG+JSON), they are *runtime* layered on top.
- [Save/Load](save-load.md) — modifiers are not persisted; mod-side persistence is on you.
- [Car Definitions](car-definitions.md) and [Asset Packs](asset-packs.md) — `IComponentBuilder` pattern, used by `MapMaskComponentBuilder` for scenery JSON definitions.
- [Multiplayer Core](multiplayer-core.md) — what MP *does* sync (KVO, request messages, `IGameMessage`); modifiers conspicuously absent.
