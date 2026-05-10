using System.Collections.Generic;
using System.Linq;
using Track;
using UnityEngine;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.TrackSwitches
{
    /// <summary>
    /// Logs the nearest TrackNodes and TrackSegments to the camera
    /// position so we can pick a target piece of track to operate on
    /// without mining the 26 MB scene file.
    ///
    /// Use: aim the camera at the spot you want, hit F8, copy the IDs
    /// from the log into the experiment's hardcoded demo location.
    /// </summary>
    internal static class PlacementHelper
    {
        private const int TopNodes = 8;
        private const int TopSegments = 8;

        public static void DumpNearestToCamera(UnityModManager.ModEntry.ModLogger log)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                log.Warning("F8: no Camera.main found.");
                return;
            }

            var graph = Graph.Shared;
            if (graph == null || !graph.HasPopulatedCollections)
            {
                log.Warning("F8: Graph.Shared not ready (no map loaded yet?).");
                return;
            }

            var camPos = cam.transform.position;
            log.Log($"F8 dump @ camera world {Fmt(camPos)} (cam fwd {Fmt(cam.transform.forward)})");

            DumpNearestNodes(graph, camPos, log);
            DumpNearestSegments(graph, camPos, log);
        }

        private static void DumpNearestNodes(Graph graph, Vector3 camPos, UnityModManager.ModEntry.ModLogger log)
        {
            var ranked = graph.Nodes
                .Where(n => n != null && n.transform != null)
                .Select(n => new { Node = n, Dist = Vector3.Distance(n.transform.position, camPos) })
                .OrderBy(x => x.Dist)
                .Take(TopNodes)
                .ToList();

            log.Log($"  --- nearest {ranked.Count} TrackNodes ---");
            foreach (var entry in ranked)
            {
                var n = entry.Node;
                var pos = n.transform.position;
                var kind = ClassifyNode(graph, n);
                var connectedIds = graph.SegmentsConnectedTo(n)
                    .Select(s => s != null ? s.id : "<null>")
                    .ToList();
                log.Log(
                    $"  node id={n.id ?? "<no-id>"} dist={entry.Dist,6:0.0}m " +
                    $"kind={kind} thrown={(n.isThrown ? "1" : "0")} " +
                    $"pos={Fmt(pos)} " +
                    $"segs=[{string.Join(",", connectedIds)}]");
            }
        }

        private static void DumpNearestSegments(Graph graph, Vector3 camPos, UnityModManager.ModEntry.ModLogger log)
        {
            var ranked = graph.Segments
                .Where(s => s != null && s.a != null && s.b != null)
                .Select(s =>
                {
                    var mid = (s.a.transform.position + s.b.transform.position) * 0.5f;
                    return new
                    {
                        Segment = s,
                        Dist = Vector3.Distance(mid, camPos),
                        Mid = mid
                    };
                })
                .OrderBy(x => x.Dist)
                .Take(TopSegments)
                .ToList();

            log.Log($"  --- nearest {ranked.Count} TrackSegments ---");
            foreach (var entry in ranked)
            {
                var s = entry.Segment;
                var len = Vector3.Distance(s.a.transform.position, s.b.transform.position);
                log.Log(
                    $"  seg id={s.id ?? "<no-id>"} dist={entry.Dist,6:0.0}m " +
                    $"len~{len,5:0.0}m " +
                    $"a={s.a.id ?? "<no-id>"} b={s.b.id ?? "<no-id>"} " +
                    $"style={s.style} pri={s.priority} spd={s.speedLimit} " +
                    $"group={s.groupId ?? "-"} " +
                    $"mid={Fmt(entry.Mid)}");
            }
        }

        private static string ClassifyNode(Graph graph, TrackNode n)
        {
            if (graph.DecodeSwitchAt(n, out _, out _, out _)) return "switch";
            var count = graph.SegmentsConnectedTo(n).Count;
            if (count == 0) return "orphan";
            if (count == 1) return "deadend";
            return "joint";
        }

        private static string Fmt(Vector3 v) => $"({v.x:0.0},{v.y:0.0},{v.z:0.0})";
    }
}
