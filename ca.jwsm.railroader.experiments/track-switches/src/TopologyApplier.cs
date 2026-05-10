using System.Collections.Generic;
using Track;
using UnityEngine;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.TrackSwitches
{
    /// <summary>
    /// Applies a <see cref="Topology"/> spec to the live <see cref="Graph"/>.
    /// Idempotent: running twice with the same topology is a no-op.
    /// After topology nodes/segments are added, triggers a full
    /// <see cref="TrackObjectManager.Rebuild"/> so the new visuals
    /// (switch geometry, spur rails, bumpers) actually appear -
    /// <see cref="TrackObjectManager.SetNeedsRebuild"/> alone wasn't
    /// enough because its partial-rebuild path filters out new
    /// segments/switches that didn't have prior descriptors.
    /// </summary>
    internal static class TopologyApplier
    {
        public struct ApplyResult
        {
            public int NodesAdded;
            public int NodesSkipped;
            public int SegmentsAdded;
            public int SegmentsSkipped;
            public bool RebuildIssued;
        }

        public static ApplyResult Apply(Topology topo, UnityModManager.ModEntry.ModLogger log)
        {
            var result = new ApplyResult();

            var graph = Graph.Shared;
            if (graph == null || !graph.HasPopulatedCollections)
            {
                log.Warning("TopologyApplier: graph not ready.");
                return result;
            }

            // Diagnostic so we can tell parse-failed-silently apart from no-changes-needed.
            log.Log(
                $"TopologyApplier: parsed " +
                $"EastRef={(topo.EastReference == null ? "null" : "set")} " +
                $"Nodes={(topo.Nodes == null ? "null" : topo.Nodes.Length.ToString())} " +
                $"Segments={(topo.Segments == null ? "null" : topo.Segments.Length.ToString())}");

            // Resolve "east" — horizontal direction from FromNode toward ToNode.
            // Used for all relative-azimuth placements.
            Vector3 eastDir = Vector3.right;
            if (topo.EastReference != null
                && !string.IsNullOrEmpty(topo.EastReference.FromNode)
                && !string.IsNullOrEmpty(topo.EastReference.ToNode))
            {
                var from = graph.GetNode(topo.EastReference.FromNode);
                var to   = graph.GetNode(topo.EastReference.ToNode);
                if (from == null || to == null)
                {
                    log.Error($"TopologyApplier: east reference nodes '{topo.EastReference.FromNode}' / '{topo.EastReference.ToNode}' not found.");
                    return result;
                }
                eastDir = to.transform.position - from.transform.position;
                eastDir.y = 0f;
                if (eastDir.sqrMagnitude < 1e-6f)
                {
                    log.Error("TopologyApplier: east direction has zero magnitude.");
                    return result;
                }
                eastDir.Normalize();
            }

            var addedNodes = new List<TrackNode>();
            var addedSegs  = new List<TrackSegment>();

            // Nodes
            if (topo.Nodes != null)
            {
                foreach (var spec in topo.Nodes)
                {
                    if (string.IsNullOrEmpty(spec.Id))
                    {
                        log.Warning("TopologyApplier: skipping node with empty Id.");
                        continue;
                    }
                    if (graph.GetNode(spec.Id) != null)
                    {
                        result.NodesSkipped++;
                        continue;
                    }

                    if (!ResolveNodePose(graph, eastDir, spec, log, out var pos, out var rot))
                        continue;

                    var node = graph.AddNode(spec.Id, pos, rot);
                    node.transform.SetParent(graph.transform, worldPositionStays: true);

                    if (spec.MultiStateSwitch != null && spec.MultiStateSwitch.RouteSegmentIds != null)
                    {
                        var mss = node.gameObject.AddComponent<MultiStateSwitch>();
                        mss.EnterSegmentId   = spec.MultiStateSwitch.EnterSegmentId;
                        mss.RouteSegmentIds  = spec.MultiStateSwitch.RouteSegmentIds;
                        mss.CurrentRouteIndex = 0;
                        log.Log(
                            $"  attached MultiStateSwitch on {node.id}: enter={mss.EnterSegmentId} routes=[{string.Join(",", mss.RouteSegmentIds)}]");
                    }

                    addedNodes.Add(node);
                    result.NodesAdded++;
                }
            }

            // Segments
            if (topo.Segments != null)
            {
                foreach (var spec in topo.Segments)
                {
                    if (string.IsNullOrEmpty(spec.Id))
                    {
                        log.Warning("TopologyApplier: skipping segment with empty Id.");
                        continue;
                    }
                    if (graph.GetSegment(spec.Id) != null)
                    {
                        result.SegmentsSkipped++;
                        continue;
                    }
                    var a = graph.GetNode(spec.AId);
                    var b = graph.GetNode(spec.BId);
                    if (a == null || b == null)
                    {
                        log.Error($"TopologyApplier: segment '{spec.Id}' refs missing nodes (a='{spec.AId}', b='{spec.BId}').");
                        continue;
                    }

                    var seg = graph.AddSegment(spec.Id, a, b);
                    seg.transform.SetParent(graph.transform, worldPositionStays: true);

                    if (System.Enum.TryParse<TrackSegment.Style>(spec.Style ?? "Standard", out var style))
                        seg.style = style;
                    if (System.Enum.TryParse<TrackClass>(spec.TrackClass ?? "Industrial", out var tc))
                        seg.trackClass = tc;
                    seg.speedLimit = spec.SpeedLimit;
                    seg.priority   = spec.Priority;

                    addedSegs.Add(seg);
                    result.SegmentsAdded++;
                }
            }

            if (result.NodesAdded == 0 && result.SegmentsAdded == 0)
            {
                log.Log(
                    $"TopologyApplier: no changes (nodes skipped={result.NodesSkipped}, segs skipped={result.SegmentsSkipped}).");
                return result;
            }

            // 3-way switch handling needs to bracket the rebuild:
            //   Plan BEFORE rebuild  -> populates SuppressedSegments so the
            //                           Harmony patch skips vanilla's full-length
            //                           render of our diverging segments
            //   Rebuild              -> vanilla wipes meshes + rebuilds (skipping
            //                           suppressed segments)
            //   Spawn AFTER rebuild  -> we add our switch meshes + remainder
            //                           segment meshes
            var renderer = ThreeWaySwitchRenderer.Instance;
            if (renderer != null) renderer.Plan(log);

            // Force a full rebuild. Per InvalidateFromNode's filtering, brand-new
            // descriptors that weren't in the affected set get dropped on the
            // partial path - we'd see geometry holes around our additions. The
            // full Rebuild() rewalks the entire graph, so new geometry shows up.
            var tom = TrackObjectManager.Instance;
            if (tom != null)
            {
                tom.Rebuild();
                result.RebuildIssued = true;
            }
            else
            {
                log.Warning("TopologyApplier: TrackObjectManager.Instance was null; mesh won't refresh.");
            }

            if (renderer != null) renderer.Spawn(log);

            log.Log(
                $"TopologyApplier: nodes added={result.NodesAdded} (skipped {result.NodesSkipped}), " +
                $"segments added={result.SegmentsAdded} (skipped {result.SegmentsSkipped}), " +
                $"rebuild={result.RebuildIssued}.");
            foreach (var n in addedNodes)
                log.Log($"  + node {n.id} @ ({n.transform.position.x:0.0},{n.transform.position.y:0.0},{n.transform.position.z:0.0})");
            foreach (var s in addedSegs)
                log.Log($"  + seg  {s.id} a={s.a.id} b={s.b.id} class={s.trackClass} spd={s.speedLimit}");

            return result;
        }

        private static bool ResolveNodePose(
            Graph graph, Vector3 eastDir, NodeSpec spec, UnityModManager.ModEntry.ModLogger log,
            out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;

            if (spec.UseAbsolute)
            {
                pos = spec.Position.ToUnity();
                rot = Quaternion.Euler(spec.RotationDeg.ToUnity());
                return true;
            }

            if (string.IsNullOrEmpty(spec.RelativeTo))
            {
                log.Error($"TopologyApplier: node '{spec.Id}' has no RelativeTo and UseAbsolute=false.");
                return false;
            }

            var anchor = graph.GetNode(spec.RelativeTo);
            if (anchor == null)
            {
                log.Error($"TopologyApplier: anchor node '{spec.RelativeTo}' for '{spec.Id}' not found.");
                return false;
            }

            var offsetDir = Quaternion.AngleAxis(spec.Offset.azimuthDeg, Vector3.up) * eastDir;
            pos = anchor.transform.position + offsetDir.normalized * spec.Offset.distance;
            pos.y += spec.Offset.yDelta;

            var facingDir = Quaternion.AngleAxis(spec.FacingAzimuthDeg, Vector3.up) * eastDir;
            rot = Quaternion.LookRotation(facingDir.normalized, Vector3.up);
            return true;
        }
    }
}
