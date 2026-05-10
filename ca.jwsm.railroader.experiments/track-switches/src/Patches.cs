using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Track;
using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.TrackSwitches
{
    /// <summary>
    /// Set of TrackSegments whose default full-length vanilla rendering is
    /// suppressed because we render the throat zone ourselves via custom
    /// 3-way switch meshes + the throat-onward remainder portions.
    /// </summary>
    public static class SuppressedSegments
    {
        public static readonly HashSet<TrackSegment> Set = new HashSet<TrackSegment>();
        public static bool BypassSuppression;
    }

    [HarmonyPatch(typeof(TrackObjectBuilder), nameof(TrackObjectBuilder.CreateSegmentObject), new[] { typeof(SegmentProxy) })]
    internal static class CreateSegmentObject_Patch
    {
        static bool Prefix(SegmentProxy segment, ref GameObject __result)
        {
            if (SuppressedSegments.BypassSuppression) return true;
            if (segment.Segment != null && SuppressedSegments.Set.Contains(segment.Segment))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(TrackObjectBuilder), nameof(TrackObjectBuilder.CreateSegmentMasks), new[] { typeof(SegmentProxy) })]
    internal static class CreateSegmentMasks_Patch
    {
        static bool Prefix(SegmentProxy segment, ref GameObject __result)
        {
            if (SuppressedSegments.BypassSuppression) return true;
            if (segment.Segment != null && SuppressedSegments.Set.Contains(segment.Segment))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Make Graph.IsSwitch recognize multi-state switch nodes (which have
    /// 4+ connected segments instead of vanilla's exactly-3). Without this,
    /// the overhead map and various other systems don't treat our node as
    /// a switch at all - no map indicator, no clickable switch entry.
    /// </summary>
    [HarmonyPatch(typeof(Graph), nameof(Graph.IsSwitch))]
    internal static class Graph_IsSwitch_Patch
    {
        static void Postfix(TrackNode node, ref bool __result)
        {
            if (__result) return;
            if (node == null) return;
            var mss = node.GetComponent<MultiStateSwitch>();
            if (mss != null && mss.RouteSegmentIds != null && mss.RouteSegmentIds.Length >= 2)
            {
                __result = true;
            }
        }
    }

    /// <summary>
    /// Make Graph.DecodeSwitchAt return sensible (enter, currentRoute, otherRoute)
    /// values for our multi-state nodes. Train routing calls SegmentsReachableFrom,
    /// which calls DecodeSwitchAt. Vanilla returns false for any node with !=3
    /// segments, so our 4-port gets no routing - trains hit the throat and stop.
    ///
    /// Mapping:
    ///   enter  = the EnterSegmentId segment (the approach)
    ///   a      = the segment for CurrentRouteIndex (the active diverging route)
    ///   b      = the next-index segment (so 'reversed' direction has SOMETHING to fall back to)
    ///
    /// This lets trains arriving via 'enter' continue to the active route ('a'),
    /// and trains arriving via 'a' return to 'enter'.
    /// </summary>
    [HarmonyPatch(typeof(Graph), nameof(Graph.DecodeSwitchAt))]
    internal static class Graph_DecodeSwitchAt_Patch
    {
        static bool Prefix(Graph __instance, TrackNode node,
            out TrackSegment enter, out TrackSegment a, out TrackSegment b,
            ref bool __result)
        {
            enter = null; a = null; b = null;
            if (node == null) return true;
            var mss = node.GetComponent<MultiStateSwitch>();
            if (mss == null || string.IsNullOrEmpty(mss.EnterSegmentId)
                || mss.RouteSegmentIds == null || mss.RouteSegmentIds.Length < 2)
            {
                return true; // not multi-state — let vanilla handle
            }

            enter = __instance.GetSegment(mss.EnterSegmentId);
            if (enter == null) return true;

            int idx = mss.CurrentRouteIndex;
            if (idx < 0 || idx >= mss.RouteSegmentIds.Length) idx = 0;
            a = __instance.GetSegment(mss.RouteSegmentIds[idx]);

            int otherIdx = (idx + 1) % mss.RouteSegmentIds.Length;
            b = __instance.GetSegment(mss.RouteSegmentIds[otherIdx]);

            __result = (a != null);
            return false;
        }
    }

    /// <summary>
    /// Click handler patch. Vanilla SwitchStandClick.Activate sends
    /// RequestSetSwitch(node.id, !node.isThrown) on primary click -
    /// flipping the binary state. For a node tagged with
    /// <see cref="MultiStateSwitch"/> we instead bump
    /// <see cref="MultiStateSwitch.CurrentRouteIndex"/> modulo the route
    /// count. The driver (<see cref="MultiStateSwitchStand"/>) animates
    /// the lever to match.
    ///
    /// Side-effect: we don't go through StateManager, so this is local-only
    /// (no multiplayer sync, no save). Acceptable for an experiment.
    ///
    /// SwitchStandClick is internal in Assembly-CSharp so we can't typeof()
    /// it from our mod assembly - we resolve the type by name at patch time.
    /// </summary>
    [HarmonyPatch]
    internal static class SwitchStandClick_Activate_Patch
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("SwitchStandClick");
            return AccessTools.Method(t, "Activate");
        }

        static bool Prefix(MonoBehaviour __instance, object evt)
        {
            var nodeField = AccessTools.Field(__instance.GetType(), "node");
            var node = nodeField?.GetValue(__instance) as TrackNode;
            if (node == null) return true;

            var mss = node.GetComponent<MultiStateSwitch>();
            if (mss == null || mss.RouteSegmentIds == null || mss.RouteSegmentIds.Length <= 1)
            {
                return true; // not a multi-state switch — vanilla logic
            }

            var activationProp = AccessTools.Property(evt.GetType(), "Activation");
            var activation = activationProp.GetValue(evt);
            // PickableActivation.Primary == 0
            if ((int)activation == 0)
            {
                mss.CurrentRouteIndex = (mss.CurrentRouteIndex + 1) % mss.RouteSegmentIds.Length;
                return false; // skip vanilla
            }

            // Secondary (right-click) - vanilla shows CTC controls which don't
            // apply to us. Just no-op for the experiment.
            return false;
        }
    }

    /// <summary>
    /// Tooltip patch. Vanilla shows "Switch Normal" / "Switch Reversed".
    /// For a multi-state switch we want to show the route count + which
    /// route is currently active.
    /// </summary>
    [HarmonyPatch]
    internal static class SwitchStandClick_Tooltip_Patch
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("SwitchStandClick");
            return AccessTools.PropertyGetter(t, "TooltipInfo");
        }

        static bool Prefix(MonoBehaviour __instance, ref object __result)
        {
            var nodeField = AccessTools.Field(__instance.GetType(), "node");
            var node = nodeField?.GetValue(__instance) as TrackNode;
            if (node == null) return true;

            var mss = node.GetComponent<MultiStateSwitch>();
            if (mss == null || mss.RouteSegmentIds == null || mss.RouteSegmentIds.Length <= 1)
            {
                return true;
            }

            int idx = mss.CurrentRouteIndex;
            int n = mss.RouteSegmentIds.Length;
            string activeRoute = (idx >= 0 && idx < n) ? mss.RouteSegmentIds[idx] : "?";

            // TooltipInfo is a public struct in the global namespace; we can
            // construct it via reflection and assign to __result.
            var tooltipType = AccessTools.TypeByName("TooltipInfo");
            var tooltip = System.Activator.CreateInstance(tooltipType);
            AccessTools.Field(tooltipType, "Title").SetValue(tooltip, $"3-way Switch (Route {idx + 1}/{n})");
            AccessTools.Field(tooltipType, "Text").SetValue(tooltip, $"Active: {activeRoute}\n<sprite name=\"MouseLeft\"> Cycle");
            __result = tooltip;
            return false;
        }
    }
}
