using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.TrackSwitches
{
    /// <summary>
    /// Declarative topology spec for the experiment. Loaded from
    /// <c>data/topology.json</c> at the mod's deployed location. The
    /// applier resolves <see cref="NodeSpec.RelativeTo"/> + offset
    /// against live graph state, so positions stay correct even if the
    /// vanilla map's anchor moves.
    ///
    /// Schema notes:
    /// - Positions can be absolute (<see cref="NodeSpec.Position"/>) OR
    ///   relative (<see cref="NodeSpec.RelativeTo"/> + <see cref="NodeSpec.Offset"/>).
    /// - <see cref="OffsetSpec.azimuthDeg"/> is rotation around +Y from
    ///   the topology's <see cref="EastReference"/> direction. Negative
    ///   = left of east; positive = right of east.
    /// - <see cref="NodeSpec.FacingAzimuthDeg"/> sets the new node's
    ///   transform.forward, again relative to the east reference.
    /// </summary>
    [Serializable]
    public class Topology
    {
        public string Description;
        public EastReferenceSpec EastReference;
        public NodeSpec[] Nodes;
        public SegmentSpec[] Segments;

        public static Topology LoadFromFile(string path)
        {
            // Newtonsoft, not JsonUtility: JsonUtility silently fails to
            // populate arrays of [Serializable] classes when those classes
            // contain nested structs (Vec3 here). Newtonsoft handles
            // everything we throw at it; cost is a single managed dep
            // already shipped by the game.
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<Topology>(json);
        }
    }

    [Serializable]
    public class EastReferenceSpec
    {
        public string Comment;
        public string FromNode;
        public string ToNode;
    }

    [Serializable]
    public class NodeSpec
    {
        public string Id;
        public string Comment;

        // Relative placement (preferred). Resolved at apply time.
        public string RelativeTo;
        public OffsetSpec Offset;
        public float FacingAzimuthDeg;

        // Absolute placement (use when no anchor makes sense).
        public bool UseAbsolute;
        public Vec3 Position;
        public Vec3 RotationDeg;

        // If set, this node is a multi-state switch (e.g. true 3-way). The
        // applier attaches a MultiStateSwitch component carrying these IDs.
        public MultiStateSwitchSpec MultiStateSwitch;
    }

    [Serializable]
    public class MultiStateSwitchSpec
    {
        public string EnterSegmentId;
        public string[] RouteSegmentIds;
    }

    [Serializable]
    public class OffsetSpec
    {
        public float azimuthDeg;  // 0 = along east reference; negative = left, positive = right
        public float distance;    // metres in horizontal plane
        public float yDelta;      // vertical offset in metres
    }

    [Serializable]
    public class SegmentSpec
    {
        public string Id;
        public string Comment;
        public string AId;
        public string BId;
        public string Style;        // matches TrackSegment.Style enum name
        public string TrackClass;   // matches TrackClass enum name
        public int SpeedLimit;
        public int Priority;
    }

    [Serializable]
    public struct Vec3
    {
        public float x;
        public float y;
        public float z;
        public Vector3 ToUnity() => new Vector3(x, y, z);
    }
}
