# Experiment: Track Switches

Add **real three-way switches** and **real double-slip switches** to
Railroader's track network — geometry, switch-stand interaction, and
ultimately routing.

## Question

Can we extend vanilla's track system to support multi-route junction
geometry (3-way and slip) cleanly enough that the visuals read as
prototype-correct and trains drive over them?

## What vanilla does today (survey findings — 2026-05-09)

Trackwork is **100% procedural math**. There are no authored frog,
point-rail, diamond, or turnout meshes. The only authored assets in
the switch path are:

- `Mesh/Rail.asset` — rail cross-section, extruded along curves
- `Mesh/Tie_LOD0/1.asset`, `Tie Long.asset`, `Tie Split.asset` — the
  tie meshes, instanced along curves (`Long`/`Split` are the special
  ties for the switch throat)
- `GameObject/SwitchStand_Prefab(.prefab|CTC.prefab)` and
  `Map Switch Stand.prefab` — the visible stand model + animator

Code path:

- `Track/SwitchGeometry.cs` — `Calculate(node, a, b, ...)` takes
  exactly **two** Bezier segments, finds their intersection, and
  derives stock rails (×2), closure rails (×2), point rails (×2),
  guard rails (×2), and a single 3-point frog. All math.
- `Track/TrackNode.cs` — `isThrown` is a single `bool`. No N-state.
- `Track/Graph.cs` — `DecodeSwitchAt(node, out _, out segA, out segB)`
  returns exactly two segments per switch.
- `Track/TrackObjectManager.cs` — per-node, builds one
  `SwitchDescriptor` from the (A, B) pair → one `SwitchGeometry` →
  one mesh group tagged `"TrackMeshGenerated"`. **Per-node rebuild**
  on `Graph.NodeDidChange`, which is good — replacing the descriptor
  for one node won't ripple.
- `Track/SwitchStand.cs` — MonoBehaviour with an `Animator` driven by
  a binary `"thrown"` parameter. Reads `node.isThrown` directly.

Implication: producing 3-way and slip geometry is **a code problem,
not an asset problem**. We write parallel `SwitchGeometryNWay` /
`SwitchGeometryDoubleSlip` calculators that produce equivalent
`LineCurve` sets and feed them to the existing
`TrackCurveMeshBuilder` → `Rail.asset` extrusion. No new authored
meshes required for the rails themselves.

## Proposed approach (subject to revision as we learn)

Phased so each phase has a visible result before the next starts.

1. **Scaffold + placement helper** *(this commit)*
   F8 hotkey logs the nearest TrackNodes + TrackSegments to the
   camera position. Lets us pick a target piece of track without
   guessing IDs from a 26 MB scene file. Target: a straight outside
   track inside East Whittier Interchange.
2. **Sanity replication** — produce vanilla-equivalent switch
   geometry from our mod-side code path on the chosen segment, just
   to confirm we can drive `TrackCurveMeshBuilder` ourselves.
3. **3-way geometry** — `SwitchGeometryNWay.Calculate(node, a, b, c, ...)`
   producing two-frog trackwork. Patch `TrackObjectManager.BuildDescriptors`
   to emit our descriptor when a node carries a `MultiStateNode`
   sidecar component.
4. **Cycling switch stand** — script-driven lever lerp between N rotation
   targets, indexed by `MultiStateNode.routeIndex`. No new Animator
   clip authoring; sidesteps the binary `"thrown"` parameter entirely.
5. **Routing extension** — find vanilla's switch-following code (AI /
   consist routing) that reads `node.isThrown`, teach it `routeIndex`.
   This is the largest unknown.
6. **Double-slip geometry** — once 3-way is solid, the slip is a 4-port
   variant (two crossings + cycling state machine across 4 routes).

## What's NOT in scope (yet)

- Save/load of multi-state node configurations. State is in-memory
  for the experiment; survival across reload is a production concern.
- Multiplayer sync.
- CTC integration.
- Mesh-quality polish (LOD switching, ballast/tie variation around
  the new throats).
- Map editor authoring UX. We hardcode demo placements in code; map
  authoring is a different, downstream problem.

## Status

**Phase 1+: in-world tooling and topology loader.** Overlay, picker,
nearest-dump, and a JSON-driven topology loader all working. First
topology entry adds a left-diverging spur off `Nwe2` in East Whittier;
the spur tip (`ts-spur-tip-1`) is reserved for the experimental
3-way / slip switch in later phases.

## Demo location

Anchored on vanilla node `Nwe2` (joint) in East Whittier Interchange.
"East" for relative placements is defined as the horizontal direction
from `N9s0` toward `Nwe2` (the direction the existing track was
carrying you into the joint).

Topology spec lives in [data/topology.json](data/topology.json) and
is auto-applied once on the first frame after the graph is populated.
Edit the JSON, redeploy (or hit F9 in-game) to re-apply.

## File map

```
track-switches/
├── ca.jwsm.railroader.experiments.track-switches.csproj
├── deploy.ps1                # build + copy to Mods/; -Watch for auto-redeploy
├── info.json                 # UMM manifest
├── README.md                 # this file
├── data/
│   └── topology.json         # nodes + segments to inject at runtime
└── src/
    ├── ExperimentEntry.cs    # UMM bootstrap + hotkey/click wiring + auto-apply
    ├── PlacementHelper.cs    # F8: log nearest nodes/segments to camera
    ├── Topology.cs           # POCO + JsonUtility load
    ├── TopologyApplier.cs    # apply topology to live Graph + force rebuild
    └── TrackOverlay.cs       # F7: in-world line/label overlay + Shift+Click pick
```

## In-game controls

| Key                | Action                                                      |
|--------------------|-------------------------------------------------------------|
| **F7**             | Toggle the in-world track overlay (lines + IDs, drawn through walls) |
| **F8**             | Dump 8 nearest TrackNodes / TrackSegments to the UMM log    |
| **F9**             | Reload `data/topology.json` from disk and re-apply (idempotent — only adds missing nodes/segments) |
| **Shift+LeftClick** | (Overlay on) Raycast a precise point on a segment under the cursor; logs segment id, parameter t, and the would-be split lengths |

Overlay color key:
- yellow cross = switch node
- cyan cross   = joint (intermediate)
- red cross    = dead end
- light-blue   = segment centerline (faded when far)

## Topology JSON schema

```jsonc
{
  "Description": "...",
  "EastReference": {                   // defines what 'east' means for relative placements
    "FromNode": "<vanilla node id>",
    "ToNode":   "<vanilla node id>"
  },
  "Nodes": [
    {
      "Id": "<new id>",
      "RelativeTo": "<existing node id>",
      "Offset": {
        "azimuthDeg": -25,             // 0=along east, negative=left of east, positive=right
        "distance":    30,             // metres in horizontal plane
        "yDelta":       0              // optional vertical offset
      },
      "FacingAzimuthDeg": -25          // sets the new node's transform.forward
      // Or absolute:
      // "UseAbsolute": true,
      // "Position":   { "x": 0, "y": 0, "z": 0 },
      // "RotationDeg": { "x": 0, "y": 0, "z": 0 }
    }
  ],
  "Segments": [
    {
      "Id":         "<new id>",
      "AId":        "<node id>",       // can be vanilla or topology-defined
      "BId":        "<node id>",
      "Style":      "Standard",        // matches TrackSegment.Style enum
      "TrackClass": "Industrial",      // matches TrackClass enum
      "SpeedLimit": 10,
      "Priority":   0
    }
  ]
}
```

## Known issues

- **Save/load not handled.** Topology lives in memory; if you save with
  a custom segment in place, references to it on reload may become
  orphaned. Don't save until we get to a phase that addresses this.
- **Mesh refresh uses the heavy `TrackObjectManager.Rebuild()`** path.
  This rebuilds *every* segment/switch mesh in the world after each
  apply — fine for one-shot startup, expensive if abused. The partial
  `InvalidateFromNode` path filters out brand-new descriptors, so it
  doesn't work for our case.

## Deploy

```powershell
.\deploy.ps1            # build + copy once
.\deploy.ps1 -NoBuild   # copy existing artifacts only
.\deploy.ps1 -Watch     # rebuild + redeploy on every src/info.json/csproj save
```

Override the game path with `$env:GAME_DIR` if it isn't at the default
`D:\SteamLibrary\steamapps\common\Railroader`.
