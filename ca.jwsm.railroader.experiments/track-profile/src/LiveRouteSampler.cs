using System.Collections.Generic;
using Model;
using Model.Definition;
using Track;
using Track.Signals;
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

        // World-space distance (meters) within which a signal is considered
        // "on the route" — should exceed the step size (50 ft = ~15 m) so
        // signals between adjacent steps still match.
        private const float SignalProximityThresholdM = 18f;

        /// <summary>
        /// Per-frame state remembered across calls. Includes the last-known
        /// direction (for hysteresis when stopped) and the cached scene
        /// list of signals (avoids per-refresh FindObjectsOfType cost).
        /// </summary>
        public sealed class State
        {
            public bool LastReversed;
            public List<CTCSignal> CachedSignals;
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

            // Reference: train head's absolute elevation (meters → feet).
            // Stored on the dataset and subtracted from each per-step elev
            // to give chart-friendly head-relative samples.
            var trainElevFt = graph.GetPosition(leadingLoc).y * FeetPerMeter;
            dest.TrainElevationFt = trainElevFt;

            // Walk the route ONCE, materializing (distFt, location, world
            // position) for every step. Grade samples, switch detection,
            // and signal proximity all consume this list.
            var stepList = WalkRoute(graph, leadingLoc, startFt, endFt);
            if (stepList.Count == 0) return false;

            FillElevationSamples(dest, stepList, trainElevFt);
            DetectSwitchAnnotations(graph, dest, stepList);
            DetectSignalAnnotations(state, dest, stepList);

            dest.CurrentGradePct = dest.GradeAt(0f);
            return true;
        }

        /// <summary>
        /// Walk the route from leadingLoc by SampleStepFt-sized hops over
        /// [startFt, endFt]. Returns a list of (distFt, location, world
        /// position) tuples used by every sampling pass below.
        /// </summary>
        private static List<RouteStep> WalkRoute(
            Graph graph, Location leadingLoc, float startFt, float endFt)
        {
            var step = SampleStepFt;
            int steps = Mathf.Max(2, Mathf.CeilToInt((endFt - startFt) / step) + 1);
            var list = new List<RouteStep>(steps);

            float distFt = startFt;
            for (int i = 0; i < steps; i++, distFt += step)
            {
                var loc = TryMove(graph, leadingLoc, distFt * MetersPerFoot);
                if (!loc.HasValue) break;
                list.Add(new RouteStep
                {
                    DistFt   = distFt,
                    Location = loc.Value,
                    WorldPos = graph.GetPosition(loc.Value),
                });
            }
            return list;
        }

        private struct RouteStep
        {
            public float    DistFt;
            public Location Location;
            public Vector3  WorldPos;
        }

        /// <summary>
        /// Detect switches by scanning segment transitions in the step list.
        /// At each transition, find the connecting TrackNode; if it's a
        /// switch, record an annotation. Switch state (normal vs reversed)
        /// comes from TrackNode.isThrown — false = normal/straight, true =
        /// thrown/diverging.
        ///
        /// The route projection itself follows the currently-lined direction
        /// (LocationByMoving walks the lined route), so when the player
        /// throws a switch, the next refresh produces a different stepList
        /// and the new lined elevation profile + downstream switches show
        /// up automatically.
        /// </summary>
        private static void DetectSwitchAnnotations(
            Graph graph, RouteData dest, List<RouteStep> stepList)
        {
            TrackSegment prevSeg = null;
            TrackNode lastSwitchNode = null;
            for (int i = 0; i < stepList.Count; i++)
            {
                var seg = stepList[i].Location.segment;
                if (seg == null) { prevSeg = null; continue; }

                if (prevSeg != null && seg != prevSeg)
                {
                    TrackNode crossed = null;
                    if (prevSeg.a != null && (prevSeg.a == seg.a || prevSeg.a == seg.b)) crossed = prevSeg.a;
                    else if (prevSeg.b != null && (prevSeg.b == seg.a || prevSeg.b == seg.b)) crossed = prevSeg.b;

                    if (crossed != null && crossed != lastSwitchNode && graph.IsSwitch(crossed))
                    {
                        dest.Annotations.Add(new RouteData.Annotation
                        {
                            Type      = "switch",
                            DistFt    = stepList[i].DistFt,
                            Diverging = crossed.isThrown ? "reversed" : "normal",
                        });
                        lastSwitchNode = crossed;
                    }
                    else if (crossed != null && !graph.IsSwitch(crossed))
                    {
                        lastSwitchNode = null;
                    }
                }
                prevSeg = seg;
            }
        }

        /// <summary>
        /// Discover signals near the route. For each cached CTCSignal, find
        /// the route step closest to the signal's world position. If that
        /// step is within SignalProximityThresholdM, record an annotation
        /// at that step's distFt with the signal's current aspect.
        ///
        /// Aspect is queried via the signal's parent SignalStorage's public
        /// GetSignalAspect(id) — the protected Storage field on CTCSignal
        /// isn't accessible from outside the assembly, but
        /// GetComponentInParent reaches the same singleton.
        /// </summary>
        private static void DetectSignalAnnotations(
            State state, RouteData dest, List<RouteStep> stepList)
        {
            if (state.CachedSignals == null)
            {
                state.CachedSignals = new List<CTCSignal>(
                    Object.FindObjectsOfType<CTCSignal>());
            }
            if (state.CachedSignals.Count == 0) return;

            var thresholdSq = SignalProximityThresholdM * SignalProximityThresholdM;
            for (int i = 0; i < state.CachedSignals.Count; i++)
            {
                var sig = state.CachedSignals[i];
                if (sig == null) continue;
                var sigPos = sig.transform.position;

                float bestSq = thresholdSq;
                float bestDistFt = 0f;
                bool found = false;
                for (int s = 0; s < stepList.Count; s++)
                {
                    var d = (sigPos - stepList[s].WorldPos).sqrMagnitude;
                    if (d < bestSq)
                    {
                        bestSq = d;
                        bestDistFt = stepList[s].DistFt;
                        found = true;
                    }
                }
                if (!found) continue;

                var aspect = GetSignalAspect(sig);
                dest.Annotations.Add(new RouteData.Annotation
                {
                    Type   = "signal",
                    DistFt = bestDistFt,
                    Aspect = AspectToKey(aspect),
                });
            }
        }

        private static SignalAspect GetSignalAspect(CTCSignal sig)
        {
            // SignalStorage is a parent MonoBehaviour the signal can find via
            // GetComponentInParent. Its GetSignalAspect(id) is public.
            var storage = sig.GetComponentInParent<SignalStorage>();
            if (storage == null) return SignalAspect.Stop;
            return storage.GetSignalAspect(sig.id);
        }

        private static string AspectToKey(SignalAspect a)
        {
            switch (a)
            {
                case SignalAspect.Clear:             return "clear";
                case SignalAspect.Approach:          return "approach";
                case SignalAspect.DivergingApproach: return "approach";
                case SignalAspect.DivergingClear:    return "approach";
                case SignalAspect.Restricting:       return "approach";
                case SignalAspect.Stop:
                default:                             return "stop";
            }
        }

        /// <summary>
        /// Materialize (distFt, GradePct, ElevationFtRel) for every step
        /// into RouteData.Samples. Grade is computed pairwise; first sample
        /// gets 0 by definition.
        /// </summary>
        private static void FillElevationSamples(
            RouteData dest, List<RouteStep> stepList, float trainElevFt)
        {
            float prevYm = stepList[0].WorldPos.y;
            float prevDistFt = stepList[0].DistFt;
            dest.Samples.Add(new RouteData.GradeSample
            {
                DistFt         = prevDistFt,
                GradePct       = 0f,
                ElevationFtRel = (prevYm * FeetPerMeter) - trainElevFt,
            });

            for (int i = 1; i < stepList.Count; i++)
            {
                var s = stepList[i];
                var thisYm = s.WorldPos.y;
                var horizontalM = (s.DistFt - prevDistFt) * MetersPerFoot;
                var verticalM = thisYm - prevYm;
                var grade = (horizontalM > 0.0001f) ? (verticalM / horizontalM) * 100f : 0f;

                dest.Samples.Add(new RouteData.GradeSample
                {
                    DistFt         = s.DistFt,
                    GradePct       = grade,
                    ElevationFtRel = (thisYm * FeetPerMeter) - trainElevFt,
                });

                prevYm = thisYm;
                prevDistFt = s.DistFt;
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
