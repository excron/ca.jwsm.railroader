using System.Collections.Generic;
using Track;
using UnityEngine;
using UnityEngine.Rendering;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.TrackSwitches
{
    /// <summary>
    /// In-world track network visualizer + segment picker.
    ///
    /// Drawing: GL.Lines for segment centerlines (sampled from the
    /// Bezier curve) and node-marker crosses, plus OnGUI floating
    /// labels with the segment/node IDs. Always-on-top so we can
    /// see the network through buildings/terrain while authoring.
    ///
    /// Picking: <see cref="TryPick"/> takes a world hit point and
    /// returns the nearest segment within <see cref="PickRadius"/>,
    /// the parameter t along that segment, and the projected world
    /// position. The pick keybinding (Shift+LMB) is wired in
    /// ExperimentEntry — this class just exposes the picker.
    /// </summary>
    public class TrackOverlay : MonoBehaviour
    {
        // ---- Tunables ------------------------------------------------------

        // Beyond this, segments aren't drawn. Keeps overlay readable in big yards.
        private const float MaxDrawDistance = 250f;

        // Labels only appear when zoomed in to roughly an authoring distance.
        private const float MaxLabelDistance = 80f;

        // Click-pick search radius around the world hit point.
        public const float PickRadius = 5f;

        // Curve-approximation tolerances. These are a bit coarser than vanilla
        // uses for mesh generation since we only need visually-passable lines.
        private const float ApproxFlatness = 1.001f;
        private const float ApproxMinLen   = 0.5f;
        private const int   ApproxDepth    = 12;
        private const float ApproxMaxLen   = 40f;

        private static readonly Color ColorSeg     = new Color(0.75f, 0.95f, 1.00f, 0.90f);
        private static readonly Color ColorSegFar  = new Color(0.40f, 0.50f, 0.60f, 0.40f);
        private static readonly Color ColorSwitch  = new Color(1.00f, 0.85f, 0.20f, 1.00f);
        private static readonly Color ColorJoint   = new Color(0.30f, 0.85f, 1.00f, 1.00f);
        private static readonly Color ColorDeadEnd = new Color(1.00f, 0.40f, 0.40f, 1.00f);
        private static readonly Color ColorOrphan  = new Color(0.60f, 0.60f, 0.60f, 0.60f);

        // ---- Singleton lifecycle ------------------------------------------

        public static TrackOverlay Instance { get; private set; }
        public bool ShowOverlay { get; set; } = true;

        public static TrackOverlay CreateHost()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("TrackSwitchesOverlay");
            Object.DontDestroyOnLoad(go);
            Instance = go.AddComponent<TrackOverlay>();
            return Instance;
        }

        public static void DestroyHost()
        {
            if (Instance != null)
            {
                Destroy(Instance.gameObject);
                Instance = null;
            }
        }

        // ---- Internal state -----------------------------------------------

        private Material _lineMat;
        private GUIStyle _labelStyle;
        private GUIStyle _labelShadow;
        private GUIStyle _hudStyle;

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
        }

        // ---- Rendering pass -----------------------------------------------

        private void OnRenderObject()
        {
            if (!ShowOverlay) return;

            // Some cameras render UI / shadow / etc. Restrict to main camera so
            // the overlay doesn't get drawn into off-screen RTs or HUD passes.
            if (Camera.current != Camera.main) return;

            var graph = Graph.Shared;
            if (graph == null || !graph.HasPopulatedCollections) return;

            var cam = Camera.main;
            if (cam == null) return;
            var camPos = cam.transform.position;

            _lineMat.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);

            DrawSegments(graph, camPos);
            DrawNodeMarkers(graph, camPos);

            GL.End();
            GL.PopMatrix();
        }

        private static void DrawSegments(Graph graph, Vector3 camPos)
        {
            foreach (var seg in graph.Segments)
            {
                if (seg == null || seg.a == null || seg.b == null) continue;
                var aPos = seg.a.transform.position;
                var bPos = seg.b.transform.position;
                var mid = (aPos + bPos) * 0.5f;
                var dist = Vector3.Distance(mid, camPos);
                if (dist > MaxDrawDistance) continue;

                var color = dist > MaxLabelDistance ? ColorSegFar : ColorSeg;
                GL.Color(color);

                List<Core.LinePoint> pts = null;
                try { pts = seg.Curve.Approximate(ApproxFlatness, ApproxMinLen, ApproxDepth, ApproxMaxLen); }
                catch { /* fallthrough to straight-line draw */ }

                if (pts == null || pts.Count < 2)
                {
                    GL.Vertex(aPos);
                    GL.Vertex(bPos);
                    continue;
                }

                for (int i = 0; i < pts.Count - 1; i++)
                {
                    GL.Vertex(pts[i].point);
                    GL.Vertex(pts[i + 1].point);
                }
            }
        }

        private static void DrawNodeMarkers(Graph graph, Vector3 camPos)
        {
            foreach (var node in graph.Nodes)
            {
                if (node == null || node.transform == null) continue;
                var pos = node.transform.position;
                var dist = Vector3.Distance(pos, camPos);
                if (dist > MaxDrawDistance) continue;

                GL.Color(ColorForNode(graph, node));

                // Cross marker: X in horizontal plane + small vertical tick.
                const float s = 0.6f;
                GL.Vertex(new Vector3(pos.x - s, pos.y, pos.z - s));
                GL.Vertex(new Vector3(pos.x + s, pos.y, pos.z + s));
                GL.Vertex(new Vector3(pos.x - s, pos.y, pos.z + s));
                GL.Vertex(new Vector3(pos.x + s, pos.y, pos.z - s));
                GL.Vertex(new Vector3(pos.x,     pos.y - s, pos.z));
                GL.Vertex(new Vector3(pos.x,     pos.y + s, pos.z));
            }
        }

        private static Color ColorForNode(Graph graph, TrackNode node)
        {
            if (graph.DecodeSwitchAt(node, out _, out _, out _)) return ColorSwitch;
            int n = graph.SegmentsConnectedTo(node).Count;
            if (n == 0) return ColorOrphan;
            if (n == 1) return ColorDeadEnd;
            return ColorJoint;
        }

        // ---- HUD pass: floating labels + key hints ------------------------

        private void OnGUI()
        {
            if (!ShowOverlay) return;

            EnsureGuiStyles();

            // Status hint, top-left.
            GUI.Label(new Rect(8, 8, 900, 24),
                "track-switches  |  F7 overlay  |  F8 dump  |  F9 reapply topology.json  |  Shift+Click pick segment",
                _hudStyle);

            var graph = Graph.Shared;
            if (graph == null || !graph.HasPopulatedCollections) return;

            var cam = Camera.main;
            if (cam == null) return;
            var camPos = cam.transform.position;

            // Node labels.
            foreach (var node in graph.Nodes)
            {
                if (node == null || node.transform == null) continue;
                var pos = node.transform.position;
                if (Vector3.Distance(pos, camPos) > MaxLabelDistance) continue;
                var screen = cam.WorldToScreenPoint(pos);
                if (screen.z < 0f) continue;
                DrawCenteredLabel(screen, node.id ?? "<no-id>", -10f);
            }

            // Segment labels at midpoint.
            foreach (var seg in graph.Segments)
            {
                if (seg == null || seg.a == null || seg.b == null) continue;
                var mid = (seg.a.transform.position + seg.b.transform.position) * 0.5f;
                if (Vector3.Distance(mid, camPos) > MaxLabelDistance) continue;
                var screen = cam.WorldToScreenPoint(mid);
                if (screen.z < 0f) continue;
                DrawCenteredLabel(screen, seg.id ?? "<no-id>", +10f);
            }
        }

        private void EnsureGuiStyles()
        {
            if (_labelStyle != null) return;
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };
            _labelStyle.normal.textColor = Color.white;

            _labelShadow = new GUIStyle(_labelStyle);
            _labelShadow.normal.textColor = new Color(0f, 0f, 0f, 0.9f);

            _hudStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.UpperLeft,
                fontStyle = FontStyle.Bold
            };
            _hudStyle.normal.textColor = new Color(1f, 0.95f, 0.7f, 0.95f);
        }

        private void DrawCenteredLabel(Vector3 screen, string text, float yOffset)
        {
            float x = screen.x;
            float y = Screen.height - screen.y + yOffset;
            var rect = new Rect(x - 60f, y - 9f, 120f, 18f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, _labelShadow);
            GUI.Label(rect, text, _labelStyle);
        }

        // ---- Picking -------------------------------------------------------

        public struct PickResult
        {
            public TrackSegment Segment;
            public float ParameterT;       // 0..1 along the segment's curve
            public Vector3 SnappedWorld;   // closest point on the curve to the hit
            public float DistanceFromHit;  // distance between hit point and curve
            public float DistanceFromA;    // arc-length-ish distance from a-end (in metres)
            public float SegmentLength;    // total length of the segment in metres
        }

        /// <summary>
        /// Find the nearest TrackSegment to <paramref name="hitWorld"/> within
        /// <see cref="PickRadius"/>. Returns false if nothing's close enough.
        /// </summary>
        public bool TryPick(Vector3 hitWorld, out PickResult result)
        {
            result = default;
            var graph = Graph.Shared;
            if (graph == null || !graph.HasPopulatedCollections) return false;

            TrackSegment best = null;
            float bestDist = float.PositiveInfinity;
            float bestT = 0f;
            Vector3 bestSnap = Vector3.zero;

            foreach (var seg in graph.Segments)
            {
                if (seg == null || seg.a == null || seg.b == null) continue;
                // Cheap bbox prune.
                var aPos = seg.a.transform.position;
                var bPos = seg.b.transform.position;
                var mid = (aPos + bPos) * 0.5f;
                var halfChord = Vector3.Distance(aPos, bPos) * 0.5f;
                if (Vector3.Distance(mid, hitWorld) > halfChord + PickRadius) continue;

                float t;
                Vector3 snap;
                try
                {
                    t = seg.Curve.ParameterClosestTo(hitWorld);
                    snap = seg.Curve.GetPoint(t);
                }
                catch { continue; }

                var d = Vector3.Distance(snap, hitWorld);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = seg;
                    bestT = t;
                    bestSnap = snap;
                }
            }

            if (best == null || bestDist > PickRadius) return false;

            float length;
            try { length = best.Curve.CalculateLength(); }
            catch { length = Vector3.Distance(best.a.transform.position, best.b.transform.position); }

            result = new PickResult
            {
                Segment = best,
                ParameterT = bestT,
                SnappedWorld = bestSnap,
                DistanceFromHit = bestDist,
                DistanceFromA = bestT * length,
                SegmentLength = length
            };
            return true;
        }

        public static void LogPick(UnityModManager.ModEntry.ModLogger log, PickResult p)
        {
            var aId = p.Segment.a != null ? p.Segment.a.id : "<null>";
            var bId = p.Segment.b != null ? p.Segment.b.id : "<null>";
            log.Log(
                $"PICK seg={p.Segment.id} t={p.ParameterT:0.000} " +
                $"snap=({p.SnappedWorld.x:0.0},{p.SnappedWorld.y:0.0},{p.SnappedWorld.z:0.0}) " +
                $"distFromCurve={p.DistanceFromHit:0.00}m " +
                $"distFromA={p.DistanceFromA:0.0}m of {p.SegmentLength:0.0}m " +
                $"a={aId} b={bId} style={p.Segment.style} spd={p.Segment.speedLimit}");
            log.Log(
                $"  splitting here would produce two segments of ~{p.DistanceFromA:0.0}m and " +
                $"~{(p.SegmentLength - p.DistanceFromA):0.0}m");
        }
    }
}
