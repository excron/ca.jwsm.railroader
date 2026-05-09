using RollingStock;
using Track;
using UnityEngine;
using Ca.Jwsm.Railroader.Experiments.ConsistDynamics.State;

namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics.Solver
{
    /// <summary>
    /// Phase 1 solver: treats the consist as a single rigid body sliding
    /// along the spline. One velocity, one mass-summed acceleration.
    ///
    /// Force model (intentionally crude — phase 1 is plumbing):
    ///   F_traction = throttle × ratedTE × reverser_sign
    ///   F_brake    = -trainBrake × mass × DecelTrainBrakeMaxMps2 × sign(v)
    ///   F_drag     = -DragLinearPerKg × mass × v
    /// Net acceleration = ΣF / Σm. Semi-implicit Euler.
    /// </summary>
    internal static class RigidConsistSolver
    {
        private const float DecelTrainBrakeMaxMps2 = 1.5f;
        private const float DragLinearPerKg        = 0.0008f;

        // Rate-limited per-consist diagnostic. One line per second per
        // active consist so logs stay readable.
        private static float _nextLogTime;

        public static void Step(ManagedConsist consist, float dt)
        {
            float v = consist.Velocity;

            float reverserSign = Mathf.Abs(consist.Reverser) < 0.01f ? 0f : Mathf.Sign(consist.Reverser);
            float fTraction = consist.Throttle * consist.RatedTeNewtons * reverserSign;

            float fBrake = -consist.TrainBrake * consist.TotalMassKg * DecelTrainBrakeMaxMps2
                         * (Mathf.Abs(v) < 1e-4f ? 0f : Mathf.Sign(v));
            float maxBrakeMag = Mathf.Abs(v) * consist.TotalMassKg / Mathf.Max(dt, 1e-4f);
            fBrake = Mathf.Sign(fBrake) * Mathf.Min(Mathf.Abs(fBrake), maxBrakeMag);

            float fDrag = -DragLinearPerKg * consist.TotalMassKg * v;

            float netForce = fTraction + fBrake + fDrag;
            float a = netForce / Mathf.Max(consist.TotalMassKg, 1f);

            v += a * dt;
            consist.Velocity = v;

            float ds = v * dt;
            WriteCarPositions(consist, ds);

            if (Time.realtimeSinceStartup >= _nextLogTime)
            {
                _nextLogTime = Time.realtimeSinceStartup + 1f;
                ExperimentEntry.Mod?.Logger?.Log(
                    $"[solver] thr={consist.Throttle:F2} rev={consist.Reverser:F2} tb={consist.TrainBrake:F2} " +
                    $"v={v:F3}m/s ds={ds:F4}m fT={fTraction:F0}N fB={fBrake:F0}N a={a:F3}m/s² " +
                    $"mass={consist.TotalMassKg:F0}kg TE={consist.RatedTeNewtons:F0}N");
            }
        }

        /// <summary>
        /// Phase 1 rigid writeback: every car advances by ds along its own
        /// spline location. No anchor math, no offset arithmetic — each
        /// car simply moves along the rails it's already on.
        /// </summary>
        private static void WriteCarPositions(ManagedConsist consist, float dsMeters)
        {
            if (Mathf.Abs(dsMeters) < 1e-6f) return;

            var graph = Graph.Shared;
            if (graph == null) return;

            foreach (var car in consist.Cars)
            {
                var newLoc = graph.LocationByMoving(car.WheelBoundsF, dsMeters);
                car.PositionWheelBoundsFront(newLoc, graph, MovementInfo.Zero, update: true);
            }
        }
    }
}
