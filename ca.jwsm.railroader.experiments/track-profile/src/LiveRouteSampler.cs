using Model;
using Model.Definition;
using Track;
using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// Reads live game state (selected locomotive + Track.Graph) and populates
    /// a RouteData with grade samples + consist + annotations relative to the
    /// train head.
    ///
    /// Coordinate frame: train-head-relative.
    ///   distFt = 0       → leading edge of the leading car
    ///   distFt &lt; 0       → behind the head (covers consist + behind buffer)
    ///   distFt &gt; 0       → lookahead
    ///
    /// We assume the SELECTED locomotive is the head of train. Real head-of-
    /// train detection (e.g., when a loco mid-consist is selected) would walk
    /// EnumerateCoupled and pick the leading-direction-most-coupled car —
    /// future work; for now this matches the typical "driving the loco at the
    /// front" case.
    ///
    /// Direction of travel is derived from car.velocity sign with a small
    /// dead-band around zero (uses last-known direction when stationary).
    /// </summary>
    public static class LiveRouteSampler
    {
        public const float MetersPerFoot = 0.3048f;
        public const float SampleStepFt = 50f;
        public const float LookaheadFt = 5280f;
        public const float BehindFt = 200f;

        // Velocity threshold (m/s) below which we treat the train as stopped
        // and fall back to last-known direction. ~0.022 m/s = ~0.05 mph.
        private const float StoppedVelocityThreshold = 0.05f;

        /// <summary>
        /// Per-frame state remembered across calls so we can pick a sensible
        /// direction when stopped.
        /// </summary>
        public sealed class State
        {
            public bool LastReversed;
        }

        /// <summary>
        /// Returns false if the loco isn't usable for sampling (null,
        /// not-a-locomotive, in Bardo, missing graph, etc.). When false the
        /// caller hides the panel.
        /// </summary>
        public static bool Sample(Car loco, RouteData dest, State state)
        {
            if (loco == null) return false;
            if (!loco.IsLocomotive) return false;
            if (loco.IsInBardo) return false;

            var graph = Graph.Shared;
            if (graph == null) return false;

            // Determine reversed state from velocity sign with hysteresis.
            // Positive velocity = moving toward F end → F is leading → not reversed.
            // Negative velocity = moving toward R end → R is leading → reversed.
            // Stationary: keep last known.
            var v = loco.velocity;
            bool reversed = state.LastReversed;
            if (v > StoppedVelocityThreshold)       reversed = false;
            else if (v < -StoppedVelocityThreshold) reversed = true;
            state.LastReversed = reversed;

            // Leading end of THIS loco. (We assume this loco is the head-of-train.)
            var leadEnd = reversed ? Car.End.R : Car.End.F;
            var leadingLoc = loco.LocationFor(leadEnd);

            // Reset the route data into train-head-relative frame.
            dest.Reset();
            dest.Name              = loco.DisplayName ?? loco.id;
            dest.Description       = "live";
            dest.LengthFt          = 1e7f;            // any large value; we use distFt = 0 as the head
            dest.MapMaxGradePct    = 4.5f;            // TODO: cache from Graph at MapDidLoad
            dest.InitialHeadPositionFt = 0f;

            // ---- Consist ----
            // EnumerateCoupled yields the entire connected consist in
            // integration-set order. We walk it and record vehicles ordered
            // tail→head from the LEADING end's perspective.
            //
            // For first cut we just take EnumerateCoupled(LogicalEnd.A) which
            // gives a stable order. Reverser-flip in the existing renderer
            // handles the visual flip so engines end up on the correct side.
            float consistLengthFt = 0f;
            foreach (var car in loco.EnumerateCoupled(Car.LogicalEnd.A))
            {
                var def = car.Definition;
                var lengthM = def?.Length ?? 12f;     // fallback ~40ft if unknown
                var lengthFt = lengthM / MetersPerFoot;
                consistLengthFt += lengthFt;

                dest.Consist.Add(new RouteData.Vehicle
                {
                    Id        = car.id ?? "",
                    Kind      = ArchetypeToKind(car.Archetype),
                    LengthFt  = lengthFt,
                    ShortName = ShortNameFor(car),
                });
            }

            // ---- Grade samples ----
            // Walk the route from (head - consistLength - behind) to (head + lookahead),
            // sampling Y at each step. Grade = ΔY / Δhorizontal.
            //
            // We sample in equally-spaced steps along the route (LocationByMoving),
            // so the actual horizontal Δ is the sample step (in meters). The
            // graph returns world-space positions (meters), Y is altitude.
            var startFt = -consistLengthFt - BehindFt;
            var endFt   = LookaheadFt;
            SampleGradesAlongRoute(graph, leadingLoc, dest, startFt, endFt);

            // ---- Annotations ----
            // Live annotation discovery (signals/switches/stations) is more
            // involved; deferring to a follow-up. Empty list for now.
            // dest.Annotations is already cleared by Reset().

            return true;
        }

        /// <summary>
        /// Sample grade % at SampleStepFt intervals from (head + startFt) to
        /// (head + endFt). For each pair of adjacent location-on-track, the
        /// grade is (Δy / Δhorizontal) × 100, with Y from Graph.GetPosition.
        ///
        /// We use Graph.LocationByMoving with Clamp end-of-track handling so
        /// running off the rails doesn't throw — the location pins at the end
        /// and subsequent samples report 0% grade (a flatline at the EOT).
        /// </summary>
        private static void SampleGradesAlongRoute(
            Graph graph,
            Location leadingLoc,
            RouteData dest,
            float startFt,
            float endFt)
        {
            // Pre-compute the two end Locations (start + end) by moving from
            // leadingLoc by their respective distances. Then sample inclusive.
            //
            // Note: LocationByMoving accepts negative distance (walks
            // backward). That's the natural way to get behind-train samples.
            var step = SampleStepFt;
            int steps = Mathf.Max(2, Mathf.CeilToInt((endFt - startFt) / step) + 1);

            // We get position-by-position: at each iteration we hold a
            // location and sample at it, then advance by `step` for the next
            // iteration.
            float distFt = startFt;
            var loc = TryMove(graph, leadingLoc, distFt * MetersPerFoot);
            if (!loc.HasValue) return;

            float prevY = graph.GetPosition(loc.Value).y;
            float prevDistFt = distFt;
            // First sample: zero grade by definition (no preceding sample).
            dest.Samples.Add(new RouteData.GradeSample { DistFt = distFt, GradePct = 0f });

            for (int i = 1; i < steps; i++)
            {
                distFt += step;
                var nextLoc = TryMove(graph, leadingLoc, distFt * MetersPerFoot);
                if (!nextLoc.HasValue) break;

                var thisY = graph.GetPosition(nextLoc.Value).y;
                var horizontalM = (distFt - prevDistFt) * MetersPerFoot;
                var verticalM = thisY - prevY;
                var grade = (horizontalM > 0.0001f) ? (verticalM / horizontalM) * 100f : 0f;

                dest.Samples.Add(new RouteData.GradeSample { DistFt = distFt, GradePct = grade });

                prevY = thisY;
                prevDistFt = distFt;
            }
        }

        private static Location? TryMove(Graph graph, Location start, float distanceMeters)
        {
            try
            {
                return graph.LocationByMoving(start, distanceMeters, checkSwitchAgainstMovement: false, Graph.EndOfTrackHandling.Clamp);
            }
            catch
            {
                // Defensive: any unexpected exception during route-walking
                // shouldn't crash the panel.
                return null;
            }
        }

        private static string ArchetypeToKind(CarArchetype archetype)
        {
            switch (archetype)
            {
                case CarArchetype.LocomotiveDiesel:
                case CarArchetype.LocomotiveSteam:  return "locomotive";
                case CarArchetype.Tender:           return "tender";
                case CarArchetype.Caboose:          return "caboose";
                case CarArchetype.Boxcar:           return "boxcar";
                case CarArchetype.Flat:             return "flatcar";
                case CarArchetype.Tank:             return "tankcar";
                case CarArchetype.HopperOpen:       return "hopper";
                case CarArchetype.Gondola:          return "gondola";
                case CarArchetype.Coach:            return "coach";
                case CarArchetype.Baggage:          return "baggage";
                default:                            return "car";
            }
        }

        private static string ShortNameFor(Car car)
        {
            // Prefer road number if available; fall back to last 4 chars of id.
            // CarIdent is a struct so no null-conditional; check the field.
            var roadNum = car.Ident.RoadNumber;
            if (!string.IsNullOrEmpty(roadNum)) return roadNum;
            var id = car.id ?? "";
            return id.Length <= 4 ? id : id.Substring(id.Length - 4);
        }
    }
}
