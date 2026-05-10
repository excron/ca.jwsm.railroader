using System.Collections.Generic;
using System.Reflection;
using Core;
using HarmonyLib;
using Helpers;
using Track;
using UnityEngine;
using UnityEngine.Rendering;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.TrackSwitches
{
    /// <summary>
    /// Renderer for true 3-way switches.
    ///
    /// Two-phase API used by <see cref="TopologyApplier"/>:
    ///
    ///   1. <see cref="Plan"/> - run BEFORE the vanilla rebuild. Walks
    ///      multi-state nodes, calls vanilla SwitchGeometry.Calculate
    ///      twice per node (left+mid, mid+right) to derive throat
    ///      geometry + per-segment "remainder" curves. Updates the
    ///      <see cref="SuppressedSegments"/> set so our diverging
    ///      segments are skipped by the vanilla mesh build that
    ///      follows.
    ///
    ///   2. <see cref="Spawn"/> - run AFTER the vanilla rebuild. Calls
    ///      vanilla TrackObjectBuilder.CreateSwitchObject for each frog
    ///      assembly (gets us proper rails+frog+roadbed meshes) and
    ///      builder.CreateSegmentObject for each remainder proxy
    ///      (renders the rails from the throat boundary out to the
    ///      stub tip). Calls go through <see cref="SuppressedSegments.BypassSuppression"/>
    ///      so our own calls aren't intercepted by our own prefix.
    ///
    /// A line overlay is also drawn (always-on-top, GL.Lines) so the
    /// computed centerlines are visible for sanity-checking; toggle by
    /// flipping <see cref="ShowLineOverlay"/> on the instance.
    /// </summary>
    public class ThreeWaySwitchRenderer : MonoBehaviour
    {
        public static ThreeWaySwitchRenderer Instance { get; private set; }
        public bool ShowLineOverlay { get; set; } = true;

        private static readonly Color ColorFrogA  = new Color(1.00f, 0.85f, 0.10f, 1.0f);
        private static readonly Color ColorFrogB  = new Color(1.00f, 0.45f, 0.10f, 1.0f);
        private static readonly Color ColorFrogPt = new Color(0.20f, 1.00f, 0.20f, 1.0f);

        private struct GeometryResult
        {
            public bool Ok;
            public SwitchGeometry Geom;
            public TrackSegment SegA;
            public TrackSegment SegB;
            public BezierCurve SliceACurve;
            public BezierCurve SliceBCurve;
            public List<SegmentProxy> Remainder;  // proxies for the throat-onward portions
            public string Error;
        }

        private struct NodeRender
        {
            public TrackNode Node;
            public GeometryResult LeftMid;
            public GeometryResult MidRight;
        }

        private Material _lineMat;
        private readonly List<NodeRender> _renders = new List<NodeRender>();
        private readonly List<GameObject> _spawnedMeshes = new List<GameObject>();

        private static FieldInfo _builderField;

        public static ThreeWaySwitchRenderer CreateHost()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("ThreeWaySwitchRendererHost");
            Object.DontDestroyOnLoad(go);
            Instance = go.AddComponent<ThreeWaySwitchRenderer>();
            return Instance;
        }

        private void Awake()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            _lineMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _lineMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _lineMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _lineMat.SetInt("_Cull",     (int)CullMode.Off);
            _lineMat.SetInt("_ZWrite",   0);
            _lineMat.SetInt("_ZTest",    (int)CompareFunction.Always);
        }

        private void OnDestroy()
        {
            if (_lineMat != null) Destroy(_lineMat);
            ClearSpawnedReferences();
        }

        // ---- Phase 1: Plan -------------------------------------------------

        /// <summary>
        /// Walk multi-state switch nodes, compute geometry, populate the
        /// suppression set. Run BEFORE the vanilla mesh rebuild.
        /// </summary>
        public void Plan(UnityModManager.ModEntry.ModLogger log)
        {
            // Anything we'd previously spawned is about to be destroyed by
            // the impending Rebuild() (it nukes all "TrackMeshGenerated"
            // tagged children), so just drop the stale references.
            ClearSpawnedReferences();
            _renders.Clear();
            SuppressedSegments.Set.Clear();

            var graph = Graph.Shared;
            if (graph == null || !graph.HasPopulatedCollections) return;

            int nodes = 0;
            foreach (var node in graph.Nodes)
            {
                if (node == null) continue;
                var mss = node.GetComponent<MultiStateSwitch>();
                if (mss == null) continue;
                if (mss.RouteSegmentIds == null || mss.RouteSegmentIds.Length != 3)
                {
                    log.Warning(
                        $"3-way: {node.id} expects exactly 3 routes; got {mss.RouteSegmentIds?.Length ?? 0}.");
                    continue;
                }
                nodes++;

                var route0 = graph.GetSegment(mss.RouteSegmentIds[0]);
                var route1 = graph.GetSegment(mss.RouteSegmentIds[1]);
                var route2 = graph.GetSegment(mss.RouteSegmentIds[2]);
                if (route0 == null || route1 == null || route2 == null)
                {
                    log.Error(
                        $"3-way: {node.id} missing route segment(s) " +
                        $"[{mss.RouteSegmentIds[0]},{mss.RouteSegmentIds[1]},{mss.RouteSegmentIds[2]}].");
                    continue;
                }

                var nr = new NodeRender
                {
                    Node = node,
                    LeftMid  = TryCalc(node, route0, route1, "L+M"),
                    MidRight = TryCalc(node, route1, route2, "M+R")
                };
                _renders.Add(nr);

                // Suppress vanilla full-length rendering of the diverging segments.
                // (The enter segment isn't suppressed - it's NOT one of the (a,b)
                // diverging routes in either Calculate call, so it has no slice
                // to replace; it renders normally end-to-end.)
                SuppressedSegments.Set.Add(route0);
                SuppressedSegments.Set.Add(route1);
                SuppressedSegments.Set.Add(route2);

                log.Log(
                    $"3-way Plan: {node.id} L+M={(nr.LeftMid.Ok ? "ok" : "FAIL " + nr.LeftMid.Error)} " +
                    $"M+R={(nr.MidRight.Ok ? "ok" : "FAIL " + nr.MidRight.Error)} " +
                    $"suppressed=[{route0.id},{route1.id},{route2.id}]");
            }
            log.Log($"3-way Plan: {nodes} multi-state nodes, suppressed segments={SuppressedSegments.Set.Count}");
        }

        // ---- Phase 2: Spawn ------------------------------------------------

        /// <summary>
        /// Build switch meshes (one per frog assembly) and remainder
        /// segment meshes for the throat-onward parts. Run AFTER the
        /// vanilla mesh rebuild has cleared old meshes.
        /// </summary>
        public void Spawn(UnityModManager.ModEntry.ModLogger log)
        {
            var builder = GetVanillaBuilder(log);
            if (builder == null) return;

            int switchMeshes = 0;
            int remainderMeshes = 0;
            int stands = 0;

            foreach (var nr in _renders)
            {
                if (nr.LeftMid.Ok)  switchMeshes += SpawnSwitch(builder, nr.Node, nr.LeftMid,  "L+M", log);
                if (nr.MidRight.Ok) switchMeshes += SpawnSwitch(builder, nr.Node, nr.MidRight, "M+R", log);

                // Dedupe remainders by underlying segment, keeping the SHORTER curve.
                var byId = new Dictionary<string, SegmentProxy>();
                CollectRemainders(byId, nr.LeftMid);
                CollectRemainders(byId, nr.MidRight);
                foreach (var proxy in byId.Values)
                {
                    remainderMeshes += SpawnRemainder(builder, proxy, log);
                }

                // One switch stand per multi-state node, driven by our cycling
                // driver. Position from the first available SwitchGeometry.
                var standGeom = nr.LeftMid.Ok ? nr.LeftMid : (nr.MidRight.Ok ? nr.MidRight : default);
                if (standGeom.Ok)
                {
                    var mss = nr.Node.GetComponent<MultiStateSwitch>();
                    if (mss != null) stands += SpawnSwitchStand(nr.Node, mss, standGeom, log);
                }
            }

            log.Log($"3-way Spawn: {switchMeshes} switch meshes, {remainderMeshes} remainder meshes, {stands} cycling stands.");
        }

        private int SpawnSwitchStand(TrackNode node, MultiStateSwitch mss, GeometryResult geom,
            UnityModManager.ModEntry.ModLogger log)
        {
            var profile = TrackObjectManager.Instance != null ? TrackObjectManager.Instance.profile : null;
            var prefab  = profile != null ? profile.switchStandPrefab : null;
            if (prefab == null)
            {
                log.Warning("3-way: no switchStandPrefab on profile.");
                return 0;
            }

            // standPosition/standRotation in SwitchGeometry are LOCAL to switchHome;
            // to place at the right world coords we add switchHome.
            var worldPos = geom.Geom.switchHome + geom.Geom.standPosition;
            var worldRot = geom.Geom.standRotation;

            var instance = Object.Instantiate(prefab, worldPos, worldRot);
            var go = instance.gameObject;
            go.name = $"TS-3way-stand-{node.id}";

            // Pull the animator + clip from vanilla SwitchStand BEFORE we destroy it.
            var animator = instance.animator;
            var clip     = instance.animationClip;

            // Replace vanilla SwitchStand with our cycling driver. Destroy is
            // deferred so the vanilla OnEnable already ran on instantiate; that's
            // OK because our driver won't conflict with its now-defunct playable.
            Object.Destroy(instance);

            var driver = go.AddComponent<MultiStateSwitchStand>();
            driver.Node = node;
            driver.MSS = mss;
            driver.Animator = animator;
            driver.AnimationClip = clip;

            // Wire the click handler's node ref. SwitchStandClick is internal;
            // resolve it by name and set its public 'node' field via reflection.
            var clickType = AccessTools.TypeByName("SwitchStandClick");
            if (clickType != null)
            {
                var clickComp = go.GetComponentInChildren(clickType, includeInactive: true);
                if (clickComp != null)
                {
                    var nodeField = AccessTools.Field(clickType, "node");
                    nodeField?.SetValue(clickComp, node);
                }
                else
                {
                    log.Warning($"3-way: no SwitchStandClick under spawned stand for {node.id}.");
                }
            }

            RegisterForFloatingOrigin(go);
            _spawnedMeshes.Add(go);
            log.Log($"3-way: spawned cycling stand at {worldPos} for {node.id}.");
            return 1;
        }

        private static void CollectRemainders(Dictionary<string, SegmentProxy> acc, GeometryResult r)
        {
            if (!r.Ok || r.Remainder == null) return;
            foreach (var prox in r.Remainder)
            {
                if (prox.Segment == null) continue;
                var id = prox.Segment.id;
                if (id == null) continue;
                if (!acc.TryGetValue(id, out var existing))
                {
                    acc[id] = prox;
                    continue;
                }
                // Keep the shorter (closer to other endpoint = further from switch node).
                float existingLen, newLen;
                try { existingLen = existing.Curve.CalculateLength(); }
                catch { existingLen = float.PositiveInfinity; }
                try { newLen = prox.Curve.CalculateLength(); }
                catch { newLen = float.PositiveInfinity; }
                if (newLen < existingLen) acc[id] = prox;
            }
        }

        private int SpawnSwitch(TrackObjectBuilder builder, TrackNode node, GeometryResult r, string tag,
            UnityModManager.ModEntry.ModLogger log)
        {
            int spawned = 0;
            try
            {
                var meshGo = builder.CreateSwitchObject(
                    r.Geom, node,
                    r.SegA.Curve, r.SliceACurve,
                    r.SegB.Curve, r.SliceBCurve);
                if (meshGo != null)
                {
                    meshGo.name = $"TS-3way-switch-{node.id}-{tag}";
                    RegisterForFloatingOrigin(meshGo);
                    _spawnedMeshes.Add(meshGo);
                    spawned++;
                }

                var style = (r.SegA.style == TrackSegment.Style.Yard || r.SegB.style == TrackSegment.Style.Yard)
                    ? TrackSegment.Style.Yard : TrackSegment.Style.Standard;
                var maskGo = builder.CreateSwitchMasks(
                    r.Geom, node,
                    r.SegA.Curve, r.SliceACurve,
                    r.SegB.Curve, r.SliceBCurve,
                    style);
                if (maskGo != null)
                {
                    maskGo.name = $"TS-3way-switchmask-{node.id}-{tag}";
                    RegisterForFloatingOrigin(maskGo);
                    _spawnedMeshes.Add(maskGo);
                }
            }
            catch (System.Exception ex)
            {
                log.Error($"3-way: SpawnSwitch {tag} for {node.id} threw: {ex}");
            }
            return spawned;
        }

        private int SpawnRemainder(TrackObjectBuilder builder, SegmentProxy proxy,
            UnityModManager.ModEntry.ModLogger log)
        {
            int spawned = 0;
            SuppressedSegments.BypassSuppression = true;
            try
            {
                var meshGo = builder.CreateSegmentObject(proxy);
                if (meshGo != null)
                {
                    meshGo.name = $"TS-3way-remainder-{proxy.Segment.id}";
                    RegisterForFloatingOrigin(meshGo);
                    _spawnedMeshes.Add(meshGo);
                    spawned++;
                }

                var maskGo = builder.CreateSegmentMasks(proxy);
                if (maskGo != null)
                {
                    maskGo.name = $"TS-3way-remaindermask-{proxy.Segment.id}";
                    RegisterForFloatingOrigin(maskGo);
                    _spawnedMeshes.Add(maskGo);
                }
            }
            catch (System.Exception ex)
            {
                log.Error($"3-way: SpawnRemainder for {proxy.Segment?.id} threw: {ex}");
            }
            finally
            {
                SuppressedSegments.BypassSuppression = false;
            }
            return spawned;
        }

        // Floating-origin registration. Without this, vanilla code shifts the
        // world (every ~1500m of camera drift) by adjusting transforms in
        // WorldTransformerTargetList.Targets - and our spawned objects, not in
        // that set, get left behind in world-space while everything else moves.
        // We add directly to the static list (no catch-up shift) because our
        // positions are already in current world-space coords.
        private static void RegisterForFloatingOrigin(GameObject go)
        {
            if (go == null) return;
            WorldTransformerTargetList.Targets.Add(go.transform);
        }

        // ---- Helpers --------------------------------------------------------

        private static GeometryResult TryCalc(TrackNode node, TrackSegment a, TrackSegment b, string tag)
        {
            var r = new GeometryResult { SegA = a, SegB = b };
            try
            {
                var pa = new SegmentProxy(a);
                var pb = new SegmentProxy(b);
                r.Geom = SwitchGeometry.Calculate(node, pa, pb,
                    out var sliceA, out var sliceB, out var remainder);
                r.SliceACurve = sliceA.Curve;
                r.SliceBCurve = sliceB.Curve;
                r.Remainder = remainder;
                r.Ok = true;
            }
            catch (System.Exception ex)
            {
                r.Error = $"{tag}:{ex.Message}";
            }
            return r;
        }

        private static TrackObjectBuilder GetVanillaBuilder(UnityModManager.ModEntry.ModLogger log)
        {
            var tom = TrackObjectManager.Instance;
            if (tom == null) { log.Warning("3-way: TrackObjectManager.Instance is null."); return null; }

            if (_builderField == null)
            {
                _builderField = typeof(TrackObjectManager).GetField(
                    "_builder", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            if (_builderField == null)
            {
                log.Error("3-way: couldn't find TrackObjectManager._builder via reflection.");
                return null;
            }
            var builder = _builderField.GetValue(tom) as TrackObjectBuilder;
            if (builder == null)
            {
                log.Warning("3-way: TrackObjectManager._builder is null (rebuild not yet run?).");
            }
            return builder;
        }

        private void ClearSpawnedReferences()
        {
            // Unregister our transforms from the floating-origin shift list.
            // The GameObjects themselves are about to be DestroyImmediate'd by
            // vanilla DestroyMesh() (tagged TrackMeshGenerated) - the Targets
            // HashSet would dangle on null entries otherwise.
            foreach (var go in _spawnedMeshes)
            {
                if (go != null) WorldTransformerTargetList.Targets.Remove(go.transform);
            }
            _spawnedMeshes.Clear();
        }

        // ---- Line overlay (always-on-top sanity check) ---------------------

        private void OnRenderObject()
        {
            if (!ShowLineOverlay) return;
            if (Camera.current != Camera.main) return;
            if (_renders.Count == 0) return;

            _lineMat.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);

            foreach (var nr in _renders)
            {
                if (nr.LeftMid.Ok)  DrawGeometry(nr.LeftMid.Geom,  ColorFrogA);
                if (nr.MidRight.Ok) DrawGeometry(nr.MidRight.Geom, ColorFrogB);
            }

            GL.End();
            GL.PopMatrix();
        }

        private static void DrawGeometry(SwitchGeometry g, Color railColor)
        {
            GL.Color(railColor);
            DrawLineCurve(g.leftStockRail);
            DrawLineCurve(g.rightStockRail);
            DrawLineCurve(g.aClosureRail);
            DrawLineCurve(g.bClosureRail);
            DrawLineCurve(g.aPointRail);
            DrawLineCurve(g.bPointRail);
            DrawLineCurve(g.leftGuardRail);
            DrawLineCurve(g.rightGuardRail);

            if (g.frogPoints != null && g.frogPoints.Length == 3)
            {
                GL.Color(ColorFrogPt);
                GL.Vertex(g.frogPoints[0].point);
                GL.Vertex(g.frogPoints[1].point);
                GL.Vertex(g.frogPoints[1].point);
                GL.Vertex(g.frogPoints[2].point);
                GL.Vertex(g.frogPoints[2].point);
                GL.Vertex(g.frogPoints[0].point);
            }
        }

        private static void DrawLineCurve(LineCurve curve)
        {
            if (curve == null) return;
            LinePoint? prev = null;
            foreach (var pt in curve.Points)
            {
                if (prev.HasValue)
                {
                    GL.Vertex(prev.Value.point);
                    GL.Vertex(pt.point);
                }
                prev = pt;
            }
        }
    }
}
