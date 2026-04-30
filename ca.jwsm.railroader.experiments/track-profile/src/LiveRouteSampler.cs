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
        public const float FeetPerMeter  = 1f / MetersPerFoot;
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
            dest.SelectedLocoLabel = loco.DisplayName ?? loco.id ?? "";

            // ---- Consist ----
            // We need to yield cars in TAIL→HEAD order so the renderer can
            // walk them left→right with the leading end (head) on the right.
            //
            // Mapping from Car.End / direction-of-travel to IntegrationSet
            // LogicalEnd:
            //   leading = F if !reversed, R if reversed
            //   FrontIsA controls how F/R align with set's A/B:
            //     FrontIsA=true  → F=A, R=B
            //     FrontIsA=false → F=B, R=A
            //
            // EnumerateCoupled(LogicalEnd.A) yields cars in A→B order.
            // EnumerateCoupled(LogicalEnd.B) yields cars in B→A order.
            //
            // To get tail→head: enumerate FROM the trailing logical end, which
            // is the OPPOSITE of the leading logical end.
            var leadingLogical = (loco.FrontIsA != reversed) ? Car.LogicalEnd.A : Car.LogicalEnd.B;
            var tailEnumerateFrom = (leadingLogical == Car.LogicalEnd.A) ? Car.LogicalEnd.B : Car.LogicalEnd.A;

            float consistLengthFt = 0f;
            foreach (var car in loco.EnumerateCoupled(tailEnumerateFrom))
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
            // First POI type: switches detected by walking the route and
            // recording each transition between adjacent track segments
            // whose connecting node has 3 connections (per Graph.IsSwitch).
            // Signals/stations/industries are deferred — those need either
            // scene-side indexing or world-position-to-route projection,
            // which is more involved.
            SampleSwitchesAlongRoute(graph, leadingLoc, dest, startFt, endFt);

            return true;
        }

        /// <summary>
        /// Walk the route at SampleStepFt resolution and record an annotation
        /// at every segment transition where the connecting node is a switch.
        /// Distance precision is the step size (~50 ft) — fine for visual
        /// markers that just need to land near the right spot.
        /// </summary>
        private static void SampleSwitchesAlongRoute(
            Graph graph,
            Location leadingLoc,
            RouteData dest,
            float startFt,
            float endFt)
        {
            var step = SampleStepFt;
            TrackSegment prevSeg = null;
            TrackNode lastSwitchNode = null;       // dedup back-and-forth zigzags

            for (float distFt = startFt; distFt <= endFt; distFt += step)
            {
                var loc = TryMove(graph, leadingLoc, distFt * MetersPerFoot);
                if (!loc.HasValue) break;
                var seg = loc.Value.segment;
                if (seg == null) { prevSeg = null; continue; }

                if (prevSeg != null && seg != prevSeg)
                {
                    // Find the node shared between prevSeg and seg.
                    TrackNode crossed = null;
                    if (prevSeg.a != null && (prevSeg.a == seg.a || prevSeg.a == seg.b)) crossed = prevSeg.a;
                    else if (prevSeg.b != null && (prevSeg.b == seg.a || prevSeg.b == seg.b)) crossed = prevSeg.b;

                    if (crossed != null && crossed != lastSwitchNode && graph.IsSwitch(crossed))
                    {
                        dest.Annotations.Add(new RouteData.Annotation
                        {
                            Type   = "switch",
                            DistFt = distFt,
                            Label  = crossed.id ?? "SW",
                        });
                        lastSwitchNode = crossed;
                    }
                    else if (crossed != null && !graph.IsSwitch(crossed))
                    {
                        lastSwitchNode = null;     // reset dedup outside of switch territory
                    }
                }
                prevSeg = seg;
            }
        }

        /// <summary>
        /// Sample elevation + grade at SampleStepFt intervals from
        /// (head + startFt) to (head + endFt). Both fields are populated on
        /// each GradeSample:
        ///   - ElevationFtRel: feet above/below the train head's current elev.
        ///     The chart plots this as the track line.
        ///   - GradePct: local slope between this sample and the previous,
        ///     in percent. Used for the grade readout.
        ///
        /// The reference elevation is sampled FIRST at the train head
        /// (distFt = 0); subsequent samples subtract that to get relative.
        ///
        /// We use Graph.LocationByMoving with Clamp end-of-track handling so
        /// running off the rails doesn't throw — the location pins at the
        /// end and subsequent samples flatline at the EOT elevation.
        /// </summary>
        private static void SampleGradesAlongRoute(
            Graph graph,
            Location leadingLoc,
            RouteData dest,
            float startFt,
            float endFt)
        {
            // Reference: train head's absolute elevation (meters → feet).
            var trainElevM = graph.GetPosition(leadingLoc).y;
            var trainElevFt = trainElevM * FeetPerMeter;
            dest.TrainElevationFt = trainElevFt;

            var step = SampleStepFt;
            int steps = Mathf.Max(2, Mathf.CeilToInt((endFt - startFt) / step) + 1);

            float distFt = startFt;
            var loc = TryMove(graph, leadingLoc, distFt * MetersPerFoot);
            if (!loc.HasValue) return;

            float prevYm = graph.GetPosition(loc.Value).y;
            float prevDistFt = distFt;

            dest.Samples.Add(new RouteData.GradeSample
            {
                DistFt         = distFt,
                GradePct       = 0f,
                ElevationFtRel = (prevYm * FeetPerMeter) - trainElevFt,
            });

            for (int i = 1; i < steps; i++)
            {
                distFt += step;
                var nextLoc = TryMove(graph, leadingLoc, distFt * MetersPerFoot);
                if (!nextLoc.HasValue) break;

                var thisYm = graph.GetPosition(nextLoc.Value).y;
                var horizontalM = (distFt - prevDistFt) * MetersPerFoot;
                var verticalM = thisYm - prevYm;
                var grade = (horizontalM > 0.0001f) ? (verticalM / horizontalM) * 100f : 0f;

                dest.Samples.Add(new RouteData.GradeSample
                {
                    DistFt         = distFt,
                    GradePct       = grade,
                    ElevationFtRel = (thisYm * FeetPerMeter) - trainElevFt,
                });

                prevYm = thisYm;
                prevDistFt = distFt;
            }

            // Current grade at the train head: interpolate from the bracketing
            // samples around distFt = 0. Cheap — most lookahead window has
            // distFt = 0 inside it.
            dest.CurrentGradePct = dest.GradeAt(0f);
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
