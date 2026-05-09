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
        public static float DecelTrainBrakeMaxMps2 = 1.5f;
        public static float DragLinearPerKg        = 0.0008f;

        // ---- At-rest gating (per consist) ----
        public static float AtRestVelocityEps = 0.01f;
        public static float InputEps          = 0.001f;

        // ---- Constants ----
        public const float G = 9.80665f;
    }
}
