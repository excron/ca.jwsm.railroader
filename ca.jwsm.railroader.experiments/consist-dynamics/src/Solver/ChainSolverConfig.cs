namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics.Solver
{
    /// <summary>
    /// Tunable knobs for the chain solver. Centralized so we can flip them
    /// at runtime without recompiling.
    /// </summary>
    internal static class ChainSolverConfig
    {
        // ---- Coupler model: dead zone with hard stops ----
        //
        // Matches vanilla's IntegrateConstraints behavior:
        //   |stretch| <= SlackLimit:  zero force, free drift (no constraint)
        //   |stretch| >  SlackLimit:  hard spring + damping (near-rigid wall)
        //
        // Inside the slack window, cars decouple entirely — adjacent cars
        // move independently, only responding to their own external forces.
        // At the wall, a high-stiffness spring snaps the system into rigid
        // chain behavior (bottomed-out, slack action complete).
        //
        // This dead-zone-with-walls model mirrors real AAR draft gear (free
        // play in the housing, metal-on-metal at the limits) and matches
        // what vanilla's renderer expects to see — no soft-regime drift.
        //
        // The implicit tridiagonal solve handles regime transitions cleanly:
        // β_i = 0 inside slack (decoupled rows in the matrix), β_i = large
        // at the wall (strong off-diagonal coupling). Single O(N) sweep.

        public static float CouplerSlackLimitMeters = 0.04f;   // 4 cm — typical AAR Type E

        public static float CouplerStiffnessHard = 5.0e8f;     // N/m, near-rigid past slack limit
        public static float CouplerDampingHard   = 1.0e6f;     // N·s/m, dissipates wall impacts

        // ---- External forces ----
        public static float DragLinearPerKg = 0.0008f;

        // ---- Air system (phase 3a: 1D pressure field along consist) ----
        //
        // Brake pipe modeled as 1D diffusion field. Per-car nodes connected
        // by an effective diffusion coefficient. Implicit tridiagonal solve
        // (Thomas) — same solver pattern as the chain dynamics.
        //
        // Lead loco's TrainBrake KVO drives a Dirichlet BC: target pipe
        // pressure at the lead's node. Diffusion propagates the pressure
        // change rearward; the rate of propagation is the visible "wave"
        // through the train.

        public const float ChargePressurePsi    = 90f;     // charged brake pipe at rest
        public const float CylinderMaxPsi       = 64f;     // full-service cylinder
        public const float CylinderRatio        = 2.46f;   // P_cyl_target = (90 - P_pipe) * ratio
        public static float CylinderTimeConstantSec = 1.5f; // exponential lag of cyl toward target

        public static float PipeDiffusionM2PerSec = 2.0e5f; // diffusion coefficient (tune for visible wave)
        public static float PipeNominalSpacingM   = 15f;    // avg car length, used as Δs in field discretization

        public static float BrakeForceMaxDecelMps2 = 1.5f;  // applied at cylinder = max

        // ---- At-rest gating (per consist) ----
        public static float AtRestVelocityEps = 0.01f;
        public static float InputEps          = 0.001f;

        // ---- Constants ----
        public const float G = 9.80665f;
    }
}
