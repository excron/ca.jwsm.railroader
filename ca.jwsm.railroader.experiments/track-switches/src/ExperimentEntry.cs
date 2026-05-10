using System.IO;
using System.Reflection;
using HarmonyLib;
using Track;
using UnityEngine;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.TrackSwitches
{
    /// <summary>
    /// UMM entry point for the track-switches experiment.
    ///
    /// Behaviours:
    ///   - On scene ready (Camera.main + Graph.Shared.HasPopulatedCollections):
    ///       * Attach the in-world overlay
    ///       * Auto-apply data/topology.json once
    ///   - F7              Toggle the in-world track overlay
    ///   - F8              Dump nearest TrackNodes/TrackSegments to log
    ///   - F9              Re-apply data/topology.json (idempotent; reads file fresh)
    ///   - Shift+LeftClick (overlay on) Pick a precise point on a segment
    /// </summary>
    public static class ExperimentEntry
    {
        public static UnityModManager.ModEntry Mod { get; private set; }

        private const KeyCode ToggleOverlayKey  = KeyCode.F7;
        private const KeyCode DumpKey           = KeyCode.F8;
        private const KeyCode ReapplyTopologyKey = KeyCode.F9;

        private const float PickRayMaxDist = 5000f;

        private static bool _overlayHosted;
        private static bool _topologyAppliedThisSession;
        private static string _modDir;
        private static string _topologyPath;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;
            _modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _topologyPath = Path.Combine(_modDir, "data", "topology.json");

            try
            {
                var harmony = new Harmony(modEntry.Info.Id);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                modEntry.Logger.Log("Harmony patches applied.");
            }
            catch (System.Exception ex)
            {
                modEntry.Logger.Error($"Failed to apply Harmony patches: {ex}");
                return false;
            }

            modEntry.OnUpdate = OnUpdate;
            modEntry.Logger.Log(
                $"track-switches loaded. {ToggleOverlayKey}=overlay  {DumpKey}=dump  " +
                $"{ReapplyTopologyKey}=reapply topology  Shift+Click=pick  " +
                $"topology={_topologyPath}");
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            // Late-init: attach overlay + auto-apply topology once the world is ready.
            if (!_overlayHosted && Camera.main != null)
            {
                try
                {
                    TrackOverlay.CreateHost();
                    ThreeWaySwitchRenderer.CreateHost();
                    _overlayHosted = true;
                    modEntry.Logger.Log("Overlay + 3-way renderer hosts attached.");
                }
                catch (System.Exception ex)
                {
                    modEntry.Logger.Error($"Failed to attach overlay hosts: {ex}");
                    _overlayHosted = true;
                }
            }

            if (!_topologyAppliedThisSession
                && Graph.Shared != null
                && Graph.Shared.HasPopulatedCollections)
            {
                try { ApplyTopologyFromFile(); }
                catch (System.Exception ex) { modEntry.Logger.Error($"Auto-apply topology failed: {ex}"); }
                _topologyAppliedThisSession = true; // don't keep retrying on failure either
            }

            // Hotkeys.
            if (Input.GetKeyDown(ToggleOverlayKey))
            {
                var ov = TrackOverlay.Instance;
                if (ov != null)
                {
                    ov.ShowOverlay = !ov.ShowOverlay;
                    modEntry.Logger.Log($"Overlay: {(ov.ShowOverlay ? "on" : "off")}");
                }
            }

            if (Input.GetKeyDown(DumpKey))
            {
                try { PlacementHelper.DumpNearestToCamera(Mod.Logger); }
                catch (System.Exception ex) { modEntry.Logger.Error($"Dump failed: {ex}"); }
            }

            if (Input.GetKeyDown(ReapplyTopologyKey))
            {
                try { ApplyTopologyFromFile(); }
                catch (System.Exception ex) { modEntry.Logger.Error($"Reapply failed: {ex}"); }
            }

            if (TrackOverlay.Instance != null
                && TrackOverlay.Instance.ShowOverlay
                && Input.GetMouseButtonDown(0)
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
            {
                TryPickAtMouse(modEntry);
            }
        }

        private static void ApplyTopologyFromFile()
        {
            if (!File.Exists(_topologyPath))
            {
                Mod.Logger.Error($"Topology file not found: {_topologyPath}");
                return;
            }
            var topo = Topology.LoadFromFile(_topologyPath);
            Mod.Logger.Log($"Loaded topology: \"{topo.Description}\"");
            TopologyApplier.Apply(topo, Mod.Logger);
        }

        private static void TryPickAtMouse(UnityModManager.ModEntry modEntry)
        {
            var cam = Camera.main;
            if (cam == null) return;

            var ray = cam.ScreenPointToRay(Input.mousePosition);

            Vector3 hitWorld;
            if (Physics.Raycast(ray, out var hit, PickRayMaxDist))
            {
                hitWorld = hit.point;
            }
            else
            {
                var graph = Graph.Shared;
                if (graph == null) return;
                float yPlane = ApproximateTrackY(graph, cam.transform.position);
                if (Mathf.Abs(ray.direction.y) < 1e-4f) return;
                float t = (yPlane - ray.origin.y) / ray.direction.y;
                if (t < 0f) return;
                hitWorld = ray.origin + ray.direction * t;
            }

            var ov = TrackOverlay.Instance;
            if (ov == null) return;

            if (!ov.TryPick(hitWorld, out var pick))
            {
                modEntry.Logger.Log(
                    $"PICK miss: no segment within {TrackOverlay.PickRadius:0}m of " +
                    $"({hitWorld.x:0.0},{hitWorld.y:0.0},{hitWorld.z:0.0}). " +
                    "Click closer to a track centerline.");
                return;
            }

            TrackOverlay.LogPick(Mod.Logger, pick);
        }

        private static float ApproximateTrackY(Graph graph, Vector3 camPos)
        {
            float bestDist = float.PositiveInfinity;
            float bestY = camPos.y;
            foreach (var node in graph.Nodes)
            {
                if (node == null || node.transform == null) continue;
                var p = node.transform.position;
                var d = Vector3.Distance(p, camPos);
                if (d < bestDist) { bestDist = d; bestY = p.y; }
            }
            return bestY;
        }
    }
}
