using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.TrackSwitches
{
    /// <summary>
    /// Sidecar component attached to TrackNode GameObjects that have more
    /// than the vanilla 2 diverging routes. Vanilla treats a node as a
    /// switch only when it has exactly 3 connected segments; for our true
    /// 3-way (4 segments: 1 enter + 3 routes) we mark the node here so
    /// our renderer / decoder can recognise it.
    ///
    /// Holds the IDs of the enter segment and the diverging routes in
    /// declared order. Route order matters: it determines which is the
    /// "left", "middle", and "right" geometrically when the renderer
    /// computes the two frog assemblies.
    ///
    /// State (current selected route) lives in <see cref="CurrentRouteIndex"/>;
    /// not yet wired to any throw mechanism.
    /// </summary>
    public class MultiStateSwitch : MonoBehaviour
    {
        public string EnterSegmentId;
        public string[] RouteSegmentIds;
        public int CurrentRouteIndex;
    }
}
