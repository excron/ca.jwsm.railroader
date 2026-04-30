using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.TrackProfile
{
    /// <summary>
    /// Static dummy route dataset loaded from data/sample-route.json.
    /// Replaces live game data while we tune visuals — once the chart reads
    /// cleanly here, the real-data adapter swaps in without touching the
    /// rendering code.
    /// </summary>
    public sealed class RouteData
    {
        public string Name;
        public string Description;
        public float  LengthFt;
        public float  MapMaxGradePct;

        public List<GradeSample>  Samples;
        public List<Annotation>   Annotations;
        public List<Vehicle>      Consist;
        public float              InitialHeadPositionFt;

        // Live-data fields populated by the sampler each refresh.
        // Used by the chart's grade readouts; the ChartView itself plots
        // ElevationFtRel for the line.
        public float CurrentGradePct;
        public float TrainElevationFt;

        public float TotalConsistLengthFt
        {
            get
            {
                var total = 0f;
                foreach (var v in Consist) total += v.LengthFt;
                return total;
            }
        }

        /// <summary>
        /// Construct an empty RouteData ready to be populated by a live
        /// sampler. Avoids the JSON-load codepath when we're driven by
        /// game state rather than a baked dataset.
        /// </summary>
        public static RouteData CreateEmpty()
        {
            return new RouteData
            {
                Name        = "",
                Description = "",
                LengthFt    = 0f,
                Samples     = new System.Collections.Generic.List<GradeSample>(256),
                Annotations = new System.Collections.Generic.List<Annotation>(64),
                Consist     = new System.Collections.Generic.List<Vehicle>(64),
            };
        }

        /// <summary>
        /// Clear all per-tick data so a fresh sampler pass can repopulate
        /// without allocating new lists. Preserves capacity.
        /// </summary>
        public void Reset()
        {
            Samples.Clear();
            Annotations.Clear();
            Consist.Clear();
            CurrentGradePct = 0f;
            TrainElevationFt = 0f;
        }

        public static RouteData LoadFromFile(string jsonPath)
        {
            var json = File.ReadAllText(jsonPath);
            var data = JsonUtility.FromJson<RouteJson>(json);
            var route = new RouteData
            {
                Name                  = data.name,
                Description           = data.description,
                LengthFt              = data.lengthFt,
                MapMaxGradePct        = data.mapMaxGradePct,
                Samples               = new List<GradeSample>(data.samples?.Length ?? 0),
                Annotations           = new List<Annotation>(data.annotations?.Length ?? 0),
                Consist               = new List<Vehicle>(data.consist?.Length ?? 0),
                InitialHeadPositionFt = data.initialHeadPositionFt,
            };

            if (data.samples != null)
                foreach (var s in data.samples)
                    route.Samples.Add(new GradeSample { DistFt = s.distFt, GradePct = s.gradePct });

            if (data.annotations != null)
                foreach (var a in data.annotations)
                    route.Annotations.Add(new Annotation
                    {
                        Type      = a.type ?? "",
                        DistFt    = a.distFt,
                        Label     = a.label ?? "",
                        Aspect    = a.aspect ?? "",
                        Diverging = a.diverging ?? "",
                        Labeled   = a.labeled,
                    });

            if (data.consist != null)
                foreach (var v in data.consist)
                    route.Consist.Add(new Vehicle
                    {
                        Id        = v.id ?? "",
                        Kind      = v.kind ?? "",
                        LengthFt  = v.lengthFt,
                        ShortName = v.shortName ?? "",
                    });

            return route;
        }

        /// <summary>
        /// Returns the interpolated grade at distFt, clamped to the route bounds.
        /// O(log n) via binary search on Samples.
        /// </summary>
        public float GradeAt(float distFt)
        {
            if (Samples.Count == 0) return 0f;
            if (distFt <= Samples[0].DistFt) return Samples[0].GradePct;
            if (distFt >= Samples[Samples.Count - 1].DistFt) return Samples[Samples.Count - 1].GradePct;

            int lo = 0, hi = Samples.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (Samples[mid].DistFt <= distFt) lo = mid;
                else hi = mid;
            }

            var a = Samples[lo];
            var b = Samples[hi];
            var t = (distFt - a.DistFt) / (b.DistFt - a.DistFt);
            return Mathf.Lerp(a.GradePct, b.GradePct, t);
        }

        /// <summary>
        /// Returns the interpolated elevation (ft, relative to the train head's
        /// current elevation) at distFt. Used by the chart's track-line and
        /// fill rendering. Centered on 0 at the train head (distFt = 0).
        /// </summary>
        public float ElevationAt(float distFt)
        {
            if (Samples.Count == 0) return 0f;
            if (distFt <= Samples[0].DistFt) return Samples[0].ElevationFtRel;
            if (distFt >= Samples[Samples.Count - 1].DistFt) return Samples[Samples.Count - 1].ElevationFtRel;

            int lo = 0, hi = Samples.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (Samples[mid].DistFt <= distFt) lo = mid;
                else hi = mid;
            }

            var a = Samples[lo];
            var b = Samples[hi];
            var t = (distFt - a.DistFt) / (b.DistFt - a.DistFt);
            return Mathf.Lerp(a.ElevationFtRel, b.ElevationFtRel, t);
        }

        // ---- Runtime types (clean shape for rendering) ----

        public struct GradeSample
        {
            public float DistFt;
            public float GradePct;
            // Elevation in feet, relative to train head's current elevation.
            // 0 at the train head, positive when terrain is higher there.
            public float ElevationFtRel;
        }

        public sealed class Annotation
        {
            public string Type;       // milepost | switch | signal | station | industry | endofline
            public float  DistFt;
            public string Label;
            public string Aspect;     // for signals: clear|approach|stop
            public string Diverging;  // for switches: left|right|straight
            public bool   Labeled;    // for mileposts: full or unlabeled tick
        }

        public sealed class Vehicle
        {
            public string Id;
            public string Kind;       // locomotive | tender | boxcar | flatcar | tankcar | gondola | caboose | ...
            public float  LengthFt;
            public string ShortName;
        }

        // ---- JSON shape (mirrors data/sample-route.json) ----

#pragma warning disable CS0649
        [Serializable] private class RouteJson
        {
            public int      schemaVersion;
            public string   name;
            public string   description;
            public float    lengthFt;
            public float    mapMaxGradePct;
            public SampleJson[]     samples;
            public AnnotationJson[] annotations;
            public VehicleJson[]    consist;
            public float    initialHeadPositionFt;
        }
        [Serializable] private class SampleJson
        {
            public float distFt;
            public float gradePct;
        }
        [Serializable] private class AnnotationJson
        {
            public string type;
            public float  distFt;
            public string label;
            public string aspect;
            public string diverging;
            public bool   labeled;
        }
        [Serializable] private class VehicleJson
        {
            public string id;
            public string kind;
            public float  lengthFt;
            public string shortName;
        }
#pragma warning restore CS0649
    }
}
