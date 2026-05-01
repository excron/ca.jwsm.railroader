using System.Collections.Generic;
using Helpers;
using Model;
using Model.Definition;
using Model.Ops;
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
        // Step needs to be small enough to NOT skip past the interior
        // segments of CTC switches (typical interior 5-30 ft). At 100 ft
        // step we were missing CTC switches entirely; at 25 ft we catch
        // most. POI positions get sub-step interpolation against world
        // positions so the dots don't jitter on the step grid as the
        // train moves.
        public const float SampleStepFt = 25f;
        public const float LookaheadFt = 26400f;          // 5 miles ahead — clamps at end-of-track
        public const float BehindFt = 200f;

        // Velocity threshold (m/s) below which we treat the train as stopped
        // and fall back to last-known direction. ~0.022 m/s = ~0.05 mph.
        private const float StoppedVelocityThreshold = 0.05f;

        // Horizontal (XZ) distance (meters) within which a signal is
        // considered "on the route". Should exceed the step size (50 ft
        // ≈ 15 m) plus the typical pole-offset of a real signal alongside
        // the rail (5-10 m). Y is ignored since signals are mounted at
        // various heights above the rail.
        private const float SignalProximityThresholdM = 25f;

        /// <summary>
        /// Per-frame state remembered across calls. Includes the last-known
        /// direction (for hysteresis when stopped) and the cached scene
        /// list of signals (avoids per-refresh FindObjectsOfType cost).
        /// </summary>
        public sealed class State
        {
            public bool LastReversed;
            public List<CTCSignal>        CachedSignals;
            public List<Area>             CachedAreas;
            public List<IndustryComponent> CachedIndustryComponents;
            public List<PassengerStop>    CachedStations;
        }

        // Proximity threshold (m) for stations — generous because stations
        // can be stretched along a platform; users want them to appear once
        // the route is anywhere near the platform centerpoint.
        private const float StationProximityThresholdM = 100f;

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
            DetectAreaAnnotations(state, dest, stepList);
            DetectSpanAnnotations(state, dest, stepList);
            DetectStationAnnotations(state, dest, stepList);

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
                        // Use plain isThrown for every switch (CTC, ABS,
                        // yard manual). It's the only field that reliably
                        // reflects the physical alignment of the points,
                        // which is what determines which way the route
                        // actually goes (LocationFrom checks isThrown to
                        // pick normal vs reversed).
                        //
                        // CTCDisplayThrown looks like the right "display"
                        // field but is private-set, only updated inside
                        // the isThrown setter when IsCTCSwitchUnlocked
                        // is true (TrackNode.cs:35-38) — which is rarely
                        // the case during normal CTC operation. For
                        // typical locked CTC switches, CTCDisplayThrown
                        // is permanently stale.
                        bool displayThrown = crossed.isThrown;

                        // Use the node's actual world position to get a
                        // precise distFt instead of snapping to the step
                        // grid — eliminates per-frame jitter as the train
                        // moves through the grid.
                        var nodePos = WorldTransformer.WorldToGame(crossed.transform.position);
                        var preciseDistFt = ClosestStepDistFtInterpolated(nodePos, stepList);

                        dest.Annotations.Add(new RouteData.Annotation
                        {
                            Type      = "switch",
                            DistFt    = preciseDistFt,
                            Diverging = displayThrown ? "reversed" : "normal",
                            Label     = $"{crossed.id}|ctc={crossed.IsCTCSwitch}|unlk={crossed.IsCTCSwitchUnlocked}|t={crossed.isThrown}|d={crossed.CTCDisplayThrown}",
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
            // Re-scan if cache is null OR empty. Signals load with their
            // tiles — first-call scan can return zero before any tiles
            // have loaded; we want to retry until the scene has them.
            // Cheap check; harmless to repeat.
            if (state.CachedSignals == null || state.CachedSignals.Count == 0)
            {
                var found = Object.FindObjectsOfType<CTCSignal>();
                state.CachedSignals = new List<CTCSignal>(found);
                if (state.CachedSignals.Count == 0) return;
            }

            var thresholdSq = SignalProximityThresholdM * SignalProximityThresholdM;
            for (int i = 0; i < state.CachedSignals.Count; i++)
            {
                var sig = state.CachedSignals[i];
                if (sig == null) continue;

                // signal.transform.position is in WORLD space (with floating-
                // origin offset applied), but Graph.GetPosition returns GAME
                // space. WorldToGame bridges them.
                var sigPos = WorldTransformer.WorldToGame(sig.transform.position);

                // Threshold check first (XZ-only — signals sit on poles
                // at varying heights so the Y component would push valid
                // matches past the threshold).
                float bestSq = thresholdSq;
                bool foundStep = false;
                for (int s = 0; s < stepList.Count; s++)
                {
                    var stepPos = stepList[s].WorldPos;
                    var dx = sigPos.x - stepPos.x;
                    var dz = sigPos.z - stepPos.z;
                    var d = dx * dx + dz * dz;
                    if (d < bestSq)
                    {
                        bestSq = d;
                        foundStep = true;
                    }
                }
                if (!foundStep) continue;

                // Interpolated distFt against the two adjacent steps so
                // the dot doesn't jitter on the step grid as the train
                // moves through it.
                var preciseDistFt = ClosestStepDistFtInterpolated(sigPos, stepList);

                dest.Annotations.Add(new RouteData.Annotation
                {
                    Type   = "signal",
                    DistFt = preciseDistFt,
                    Aspect = AspectToKey(sig.CurrentAspect),
                });
            }
        }

        /// <summary>
        /// Detect contiguous "area" regions along the route. Each route
        /// step's world position is checked against every cached Area's
        /// radial Contains(point); consecutive steps in the same area are
        /// coalesced into a single annotation with a distFt range. Areas
        /// are global, render unconditionally — they tell you where the
        /// train is geographically.
        /// </summary>
        private static void DetectAreaAnnotations(
            State state, RouteData dest, List<RouteStep> stepList)
        {
            if (state.CachedAreas == null || state.CachedAreas.Count == 0)
            {
                state.CachedAreas = new List<Area>(Object.FindObjectsOfType<Area>());
                if (state.CachedAreas.Count == 0) return;
            }

            string currentAreaId = null;
            float runStartFt = 0f;

            foreach (var step in stepList)
            {
                Area matched = null;
                for (int i = 0; i < state.CachedAreas.Count; i++)
                {
                    var a = state.CachedAreas[i];
                    if (a == null) continue;
                    if (a.Contains(step.WorldPos)) { matched = a; break; }
                }
                // Vanilla convention: use area.name (the GameObject name,
                // human-readable like "Sylva" or "Nantahala") not
                // area.identifier (internal short id). Verified across
                // DailyReport, SwitchListController, StationAgent — they
                // all read area.name.
                var areaName = matched?.name;
                if (areaName != currentAreaId)
                {
                    if (currentAreaId != null)
                    {
                        dest.Annotations.Add(new RouteData.Annotation
                        {
                            Type      = "area",
                            DistFt    = runStartFt,
                            DistFtEnd = step.DistFt,
                            Label     = currentAreaId,
                            Identifier = currentAreaId,
                        });
                    }
                    currentAreaId = areaName;
                    runStartFt = step.DistFt;
                }
            }
            if (currentAreaId != null && stepList.Count > 0)
            {
                dest.Annotations.Add(new RouteData.Annotation
                {
                    Type      = "area",
                    DistFt    = runStartFt,
                    DistFtEnd = stepList[stepList.Count - 1].DistFt,
                    Label     = currentAreaId,
                    Identifier = currentAreaId,
                });
            }
        }

        /// <summary>
        /// Detect industry spans the route is currently switched into. For
        /// each route step, ask each cached IndustryComponent's TrackSpans
        /// whether they Contain(step.Location). The route projection is
        /// already lined-route-aware (LocationByMoving follows isThrown),
        /// so we naturally only see spans on tracks we're actually heading
        /// down — parallel-track spans we're not switched onto don't show.
        /// </summary>
        private static void DetectSpanAnnotations(
            State state, RouteData dest, List<RouteStep> stepList)
        {
            if (state.CachedIndustryComponents == null || state.CachedIndustryComponents.Count == 0)
            {
                state.CachedIndustryComponents = new List<IndustryComponent>(
                    Object.FindObjectsOfType<IndustryComponent>());
                if (state.CachedIndustryComponents.Count == 0) return;
            }

            string currentSpanLabel = null;
            float runStartFt = 0f;

            foreach (var step in stepList)
            {
                string matched = null;
                for (int i = 0; i < state.CachedIndustryComponents.Count; i++)
                {
                    var ic = state.CachedIndustryComponents[i];
                    if (ic == null || ic.trackSpans == null) continue;
                    for (int j = 0; j < ic.trackSpans.Length; j++)
                    {
                        var span = ic.trackSpans[j];
                        if (span == null) continue;
                        if (span.Contains(step.Location))
                        {
                            matched = ic.DisplayName;
                            break;
                        }
                    }
                    if (matched != null) break;
                }

                if (matched != currentSpanLabel)
                {
                    if (currentSpanLabel != null)
                    {
                        dest.Annotations.Add(new RouteData.Annotation
                        {
                            Type      = "span",
                            DistFt    = runStartFt,
                            DistFtEnd = step.DistFt,
                            Label     = currentSpanLabel,
                        });
                    }
                    currentSpanLabel = matched;
                    runStartFt = step.DistFt;
                }
            }
            if (currentSpanLabel != null && stepList.Count > 0)
            {
                dest.Annotations.Add(new RouteData.Annotation
                {
                    Type      = "span",
                    DistFt    = runStartFt,
                    DistFtEnd = stepList[stepList.Count - 1].DistFt,
                    Label     = currentSpanLabel,
                });
            }
        }

        /// <summary>
        /// Discover passenger stops near the route. Uses the same cache +
        /// proximity pattern as signals (XZ distance, world→game space
        /// conversion).
        /// </summary>
        private static void DetectStationAnnotations(
            State state, RouteData dest, List<RouteStep> stepList)
        {
            if (state.CachedStations == null || state.CachedStations.Count == 0)
            {
                state.CachedStations = new List<PassengerStop>(
                    Object.FindObjectsOfType<PassengerStop>());
                if (state.CachedStations.Count == 0) return;
            }

            var thresholdSq = StationProximityThresholdM * StationProximityThresholdM;
            for (int i = 0; i < state.CachedStations.Count; i++)
            {
                var stop = state.CachedStations[i];
                if (stop == null) continue;
                var stopPos = WorldTransformer.WorldToGame(stop.transform.position);

                float bestSq = thresholdSq;
                bool foundStep = false;
                for (int s = 0; s < stepList.Count; s++)
                {
                    var dx = stopPos.x - stepList[s].WorldPos.x;
                    var dz = stopPos.z - stepList[s].WorldPos.z;
                    var d = dx * dx + dz * dz;
                    if (d < bestSq) { bestSq = d; foundStep = true; }
                }
                if (!foundStep) continue;

                var preciseDistFt = ClosestStepDistFtInterpolated(stopPos, stepList);
                dest.Annotations.Add(new RouteData.Annotation
                {
                    Type   = "station",
                    DistFt = preciseDistFt,
                    Label  = stop.DisplayName,
                });
            }
        }

        /// <summary>
        /// Compute a precise distFt for a world-space POI by finding the
        /// closest step and linearly interpolating against the better
        /// adjacent neighbor. Uses XZ-only distance (Y can vary).
        ///
        /// Without interpolation, POI positions snap to the step grid and
        /// jitter by ± step_size as the grid shifts under the moving
        /// train. With interpolation, motion is sub-step smooth.
        /// </summary>
        private static float ClosestStepDistFtInterpolated(Vector3 worldPos, List<RouteStep> stepList)
        {
            if (stepList.Count == 0) return 0f;
            if (stepList.Count == 1) return stepList[0].DistFt;

            float bestSq = float.MaxValue;
            int bestIdx = 0;
            for (int i = 0; i < stepList.Count; i++)
            {
                var dx = worldPos.x - stepList[i].WorldPos.x;
                var dz = worldPos.z - stepList[i].WorldPos.z;
                var d = dx * dx + dz * dz;
                if (d < bestSq) { bestSq = d; bestIdx = i; }
            }

            // Pick the better-of-two adjacent neighbors to interpolate against.
            int neighborIdx;
            if (bestIdx == 0) neighborIdx = 1;
            else if (bestIdx == stepList.Count - 1) neighborIdx = bestIdx - 1;
            else
            {
                var dxL = worldPos.x - stepList[bestIdx - 1].WorldPos.x;
                var dzL = worldPos.z - stepList[bestIdx - 1].WorldPos.z;
                var dL = dxL * dxL + dzL * dzL;
                var dxR = worldPos.x - stepList[bestIdx + 1].WorldPos.x;
                var dzR = worldPos.z - stepList[bestIdx + 1].WorldPos.z;
                var dR = dxR * dxR + dzR * dzR;
                neighborIdx = (dL < dR) ? bestIdx - 1 : bestIdx + 1;
            }

            // Lerp t = d_to_best / (d_to_best + d_to_neighbor). When pos
            // sits exactly at a step, t=0 and we return that step's distFt.
            // When midway, t≈0.5.
            var pBest = stepList[bestIdx].WorldPos;
            var pNeigh = stepList[neighborIdx].WorldPos;
            var dBest = Mathf.Sqrt(bestSq);
            var dxN = worldPos.x - pNeigh.x;
            var dzN = worldPos.z - pNeigh.z;
            var dNeigh = Mathf.Sqrt(dxN * dxN + dzN * dzN);
            var t = dBest / (dBest + dNeigh + 0.0001f);
            return Mathf.Lerp(stepList[bestIdx].DistFt, stepList[neighborIdx].DistFt, t);
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
