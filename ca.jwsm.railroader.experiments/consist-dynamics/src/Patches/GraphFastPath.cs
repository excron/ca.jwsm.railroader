using HarmonyLib;
using Track;

namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics.Patches
{
    /// <summary>
    /// Phase 4: O(1) fast path for the common case of Graph.LocationByMoving.
    ///
    /// Vanilla's LocationByMoving walks the rail graph segment-by-segment
    /// until the requested distance is consumed. For long moves or moves
    /// that cross segment boundaries (switches, etc.) this is necessary.
    /// But for short moves that stay within a single segment — which is
    /// the overwhelming majority of calls our solver makes per tick
    /// (cars advance by v·dt, typically &lt;0.5 m, on segments tens to
    /// hundreds of meters long) — the answer is just arithmetic:
    ///
    ///     new Location(segment, distance + ds, end)
    ///
    /// We prefix-patch the canonical 5-arg overload (the 4-arg delegates
    /// to it). On a within-segment move, we compute the result directly
    /// and skip the original. On any other case (cross-segment, end of
    /// track, switch crossing) we return true and let vanilla handle it.
    ///
    /// This affects ALL callers in the game, including vanilla's own
    /// PositionWheelBoundsFront, UpdateBaseLocations, AutoEngineerPlanner,
    /// signaling, dispatch — anything that walks the graph by distance.
    /// As long as the fast-path semantics match vanilla for the
    /// within-segment case (they do — we replicate the exact branching
    /// logic from Graph.LocationByMoving lines 362-369), the patch is
    /// transparent to all callers. Slower paths fall through unchanged.
    /// </summary>
    [HarmonyPatch(typeof(Graph), nameof(Graph.LocationByMoving),
        new[] { typeof(Location), typeof(float), typeof(bool), typeof(Graph.EndOfTrackHandling) })]
    internal static class GraphLocationByMovingFastPath
    {
        private static bool Prefix(Location start, float distance, ref Location __result)
        {
            // Defer to vanilla on null segment — preserves whatever error
            // path vanilla takes (probably an exception we shouldn't mimic).
            if (start.segment == null) return true;

            if (distance == 0f)
            {
                __result = start;
                return false;
            }

            // Vanilla's logic, exact:
            //   if distance < 0: flip orientation, walk forward by |distance|,
            //                    leave result in flipped frame.
            //   if distance > 0: walk forward by distance.
            // The within-segment fast path matches Graph.LocationByMoving's
            // line 365-369 condition (`if (num2 < num3)` → stay in segment).

            if (distance > 0f)
            {
                if (distance < start.DistanceUntilEnd())
                {
                    __result = start.Moving(distance);
                    return false;
                }
            }
            else
            {
                // Vanilla flips on entry, walks forward, then flips back on
                // exit (Graph.cs:399-403). Match it exactly — without the
                // unflip, the encoding's `end` is swapped and any caller
                // that uses `end` for direction (most do) sees a result
                // pointing the wrong way.
                Location flipped = start.Flipped();
                float dPos = -distance;
                if (dPos < flipped.DistanceUntilEnd())
                {
                    __result = flipped.Moving(dPos).Flipped();
                    return false;
                }
            }

            // Cross-segment / end-of-track / switch traversal — fall through
            // to vanilla. Adds ~1 ns of patch overhead on the slow path which
            // is rare (cars hit segment boundaries only when crossing a switch).
            return true;
        }
    }
}
