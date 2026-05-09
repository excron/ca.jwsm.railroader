using UnityEngine;
using Ca.Jwsm.Railroader.Experiments.ConsistDynamics.State;

namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics.Solver
{
    /// <summary>
    /// Phase 3a: brake-pipe air model as a 1D pressure diffusion field along
    /// consist arc length. One node per car. Implicit time integration via
    /// tridiagonal Thomas solve — same structural pattern as the chain solver,
    /// O(N) per tick, no iteration.
    ///
    /// Inputs (driven by HUD KVO at the lead loco):
    ///   target pipe pressure at lead = 90 - 26·TrainBrake   (psi)
    ///
    /// Field equation (continuous):
    ///   ∂P/∂t = D · ∂²P/∂s²
    ///
    /// Discrete (per-car nodes, uniform Δs):
    ///   -α·P_{i-1}^new + (1+2α)·P_i^new - α·P_{i+1}^new = P_i^old
    ///   α = D · dt / Δs²
    ///
    /// Lead loco's node is a Dirichlet BC: row replaced with P = target.
    /// Other locos and anglecocks are phase 3b.
    ///
    /// Per-car cylinder dynamics (local, explicit Euler):
    ///   P_cyl_target = clamp((90 - P_pipe) · 2.46, 0, 64)
    ///   dP_cyl/dt    = (P_cyl_target - P_cyl) / τ
    ///
    /// Writeback: PipePressurePsi → car.air.BrakeLine.Pressure
    ///            CylinderPressurePsi → car.air.BrakeCylinder.Pressure
    /// Vanilla's HUD pill bar reads BrakeCylinder.Pressure → renders our state.
    /// </summary>
    internal static class AirPipeSolver
    {
        // Per-tick scratch buffers, sized to largest consist seen.
        private static float[] _diag   = new float[64];
        private static float[] _rhs    = new float[64];
        private static float[] _cPrime = new float[64];
        private static float[] _dPrime = new float[64];

        public static void Step(ManagedConsist consist, float dt)
        {
            int n = consist.CarCount;
            if (n == 0) return;

            EnsureBuffers(n);

            var pipe = consist.PipePressurePsi;
            var cyl  = consist.CylinderPressurePsi;

            // ---- 1. Pipe diffusion solve (implicit, tridiagonal) ----

            float D    = ChainSolverConfig.PipeDiffusionM2PerSec;
            float ds   = ChainSolverConfig.PipeNominalSpacingM;
            float alpha = D * dt / (ds * ds);

            // Default: 1 + 2α interior, 1 + α boundary. Off-diagonal = -α.
            // RHS = old pipe pressure.
            for (int i = 0; i < n; i++)
            {
                _diag[i] = 1f + 2f * alpha;
                _rhs[i]  = pipe[i];
            }
            if (n > 1)
            {
                _diag[0]     = 1f + alpha;
                _diag[n - 1] = 1f + alpha;
            }
            else
            {
                _diag[0] = 1f;
            }

            // Dirichlet BC at lead loco: force P_lead = target.
            // Compute target from lead loco's TrainBrake KVO value.
            float target = ChainSolverConfig.ChargePressurePsi - 26f * Mathf.Clamp01(consist.TrainBrake);
            int leadIdx = consist.LeadLocoIndex;
            if (leadIdx >= 0 && leadIdx < n)
            {
                // Row leadIdx → 1·P = target
                _diag[leadIdx] = 1f;
                _rhs[leadIdx]  = target;
                // Note: off-diagonals at this row will be zeroed via the
                // Thomas pass below by setting the implicit sub/sup-diagonal
                // contributions to 0 for this row.
            }

            // ---- 2. Thomas algorithm with Dirichlet decoupling at leadIdx ----
            //
            // Off-diagonal between rows i and i+1 is normally -α. At the
            // Dirichlet row (leadIdx), we want no coupling to neighbors —
            // sub- and super-diagonals at that row are zeroed.

            // Forward sweep
            float denom0 = _diag[0];
            float supDiag0 = (0 == leadIdx) ? 0f : -alpha;
            _cPrime[0] = (n > 1) ? (supDiag0 / denom0) : 0f;
            _dPrime[0] = _rhs[0] / denom0;

            for (int i = 1; i < n; i++)
            {
                float a = (i == leadIdx) ? 0f : -alpha;
                float denom = _diag[i] - a * _cPrime[i - 1];
                if (i < n - 1)
                {
                    float c = (i == leadIdx) ? 0f : -alpha;
                    _cPrime[i] = c / denom;
                }
                _dPrime[i] = (_rhs[i] - a * _dPrime[i - 1]) / denom;
            }

            // ---- 3. Fused back-sub + cylinder dynamics + vanilla writeback ----
            //
            // Three operations collapse into one backward pass:
            //   - Thomas back-sub naturally walks n-1 → 0, writing pipe[i]
            //   - Cylinder update at car i is purely local: needs pipe[i]
            //     (just written this iter) and cyl[i] (its own prev value)
            //   - Vanilla writeback is also purely local
            // All three operations are independent across cars once pipe[i]
            // is settled, so they collapse into one cache-friendly walk.

            float charge = ChainSolverConfig.ChargePressurePsi;
            float ratio  = ChainSolverConfig.CylinderRatio;
            float maxCyl = ChainSolverConfig.CylinderMaxPsi;
            float tauInv = 1f / Mathf.Max(0.01f, ChainSolverConfig.CylinderTimeConstantSec);
            float cylDtFactor = tauInv * dt;
            var carsArray = consist.CarsArray;

            // Tail car (no upstream dep in back-sub).
            {
                int i = n - 1;
                pipe[i] = _dPrime[i];

                float drop = charge - pipe[i];
                if (drop < 0f) drop = 0f;
                float targetCyl = drop * ratio;
                if (targetCyl > maxCyl) targetCyl = maxCyl;
                cyl[i] += (targetCyl - cyl[i]) * cylDtFactor;

                var car = carsArray[i];
                if (car?.air != null)
                {
                    car.air.BrakeLine.Pressure     = pipe[i];
                    car.air.BrakeCylinder.Pressure = cyl[i];
                }
            }

            // Walk forward of the tail backward toward the head.
            for (int i = n - 2; i >= 0; i--)
            {
                pipe[i] = _dPrime[i] - _cPrime[i] * pipe[i + 1];

                float drop = charge - pipe[i];
                if (drop < 0f) drop = 0f;
                float targetCyl = drop * ratio;
                if (targetCyl > maxCyl) targetCyl = maxCyl;
                cyl[i] += (targetCyl - cyl[i]) * cylDtFactor;

                var car = carsArray[i];
                if (car?.air != null)
                {
                    car.air.BrakeLine.Pressure     = pipe[i];
                    car.air.BrakeCylinder.Pressure = cyl[i];
                }
            }
        }

        private static void EnsureBuffers(int n)
        {
            if (_diag.Length < n)
            {
                int sz = Mathf.NextPowerOfTwo(n);
                _diag   = new float[sz];
                _rhs    = new float[sz];
                _cPrime = new float[sz];
                _dPrime = new float[sz];
            }
        }
    }
}
