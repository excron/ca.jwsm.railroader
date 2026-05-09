using RollingStock;
using Track;
using UnityEngine;
using Ca.Jwsm.Railroader.Experiments.ConsistDynamics.State;

namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics.Solver
{
    /// <summary>
    /// Phase 2: per-car arc-length state with compliant 1D couplers,
    /// integrated implicitly via a tridiagonal linear solve (Thomas
    /// algorithm).
    ///
    /// Coupler model is piecewise linear: soft inside the slack range,
    /// hard outside. Per-coupler stiffness varies by current stretch, so
    /// the tridiagonal matrix has non-uniform off-diagonals — but the
    /// structure is unchanged and Thomas remains O(N) with no iteration.
    ///
    /// That's the whole "Featherstone for 1D" payload: the structure of
    /// the matrix gives the answer in one O(N) sweep, regardless of
    /// coupler stiffness or how nonlinear the regime transitions are.
    /// </summary>
    internal static class ImplicitChainSolver
    {
        // Per-tick scratch buffers, sized to the largest consist seen.
        private static float[] _diag    = new float[64];   // diagonal of A, length N
        private static float[] _beta    = new float[64];   // per-coupler β, length N-1
        private static float[] _rhs     = new float[64];   // RHS, then dv after solve, length N
        private static float[] _cPrime  = new float[64];   // working super-diag in Thomas, length N-1
        private static float[] _dPrime  = new float[64];   // working RHS in Thomas, length N
        private static float[] _fOld    = new float[64];   // per-coupler explicit force F(stretch_old)

        private static float _nextLogTime;

        public static void Step(ManagedConsist consist, float dt)
        {
            int n = consist.CarCount;
            if (n == 0) return;

            EnsureBuffers(n);

            var v       = consist.Velocities;
            var m       = consist.Masses;
            var stretch = consist.Stretches;

            // ---- 1. Per-coupler stiffness/damping/force from old stretches ----
            //
            // Evaluate the piecewise force law at each coupler's current
            // stretch. Caches β_i = k_eff_i·dt² + c_eff_i·dt and F_old_i for
            // use in matrix and RHS construction below.

            for (int i = 0; i < n - 1; i++)
            {
                CouplerLaw(stretch[i], out float fOld, out float kEff, out float cEff);
                _fOld[i] = fOld;
                _beta[i] = kEff * dt * dt + cEff * dt;
            }

            // ---- 2. External forces per car (brake, drag, grade) into _rhs[] ----
            //
            // Then add traction at each loco's index. Result is F_external_i·dt.

            ComputeExternalForcesScaled(consist, dt, _rhs);

            // ---- 3. Add coupler explicit-force contributions to RHS ----
            //
            // For interior i: RHS[i] += F_old_{i-1}·dt - F_old_i·dt
            //                          + β_{i-1}·(v_{i-1} - v_i) + β_i·(v_{i+1} - v_i)
            // Boundaries omit the missing-coupler terms.

            if (n == 1)
            {
                _diag[0] = m[0];
                // _rhs[0] is just F_ext_0·dt; nothing more to add (no couplers).
            }
            else
            {
                // i == 0 (front, only right coupler β[0] connects to car 1)
                _diag[0] = m[0] + _beta[0];
                _rhs[0] += -_fOld[0] * dt + _beta[0] * (v[1] - v[0]);

                // i == n-1 (rear, only left coupler β[n-2])
                _diag[n - 1] = m[n - 1] + _beta[n - 2];
                _rhs[n - 1] += +_fOld[n - 2] * dt + _beta[n - 2] * (v[n - 2] - v[n - 1]);

                // Interior
                for (int i = 1; i < n - 1; i++)
                {
                    _diag[i] = m[i] + _beta[i - 1] + _beta[i];
                    _rhs[i] += (_fOld[i - 1] - _fOld[i]) * dt
                            + _beta[i - 1] * (v[i - 1] - v[i])
                            + _beta[i]     * (v[i + 1] - v[i]);
                }
            }

            // ---- 4. Thomas algorithm: tridiagonal solve in-place ----
            //
            // Sub- and super-diagonals are A[i,i+1] = A[i+1,i] = -β[i].
            // Stored implicitly via _beta[].

            // Forward sweep
            float denom0 = _diag[0];
            _cPrime[0] = (n > 1) ? (-_beta[0] / denom0) : 0f;
            _dPrime[0] = _rhs[0] / denom0;

            for (int i = 1; i < n; i++)
            {
                float subDiag = -_beta[i - 1];
                float denom = _diag[i] - subDiag * _cPrime[i - 1];
                if (i < n - 1)
                {
                    float supDiag = -_beta[i];
                    _cPrime[i] = supDiag / denom;
                }
                _dPrime[i] = (_rhs[i] - subDiag * _dPrime[i - 1]) / denom;
            }

            // Back-substitution → write dv into _rhs (reuse buffer)
            _rhs[n - 1] = _dPrime[n - 1];
            for (int i = n - 2; i >= 0; i--)
                _rhs[i] = _dPrime[i] - _cPrime[i] * _rhs[i + 1];

            // ---- 5. Update velocities and stretches ----
            for (int i = 0; i < n; i++)
                v[i] += _rhs[i];

            // stretch_i^new = stretch_i^old + (v_i^new - v_{i+1}^new) · dt
            for (int i = 0; i < stretch.Length; i++)
                stretch[i] += (v[i] - v[i + 1]) * dt;

            // ---- 6. Visual writeback ----
            WriteCarPositions(consist, dt);

            // ---- 7. Diagnostics, rate-limited ----
            if (Time.realtimeSinceStartup >= _nextLogTime)
            {
                _nextLogTime = Time.realtimeSinceStartup + 1f;
                LogState(consist);
            }
        }

        /// <summary>
        /// Vanilla-style dead-zone coupler force law:
        ///   |s| ≤ L: F = 0           (free slack — cars drift independently)
        ///   |s| > L: F = k_hard · (|s| - L) · sign(s)   (hard wall)
        ///
        /// Inside the dead zone, the corresponding tridiagonal row decouples
        /// from its neighbors (β_i = 0). Outside, β_i is large, snapping the
        /// chain into rigid-coupled behavior.
        ///
        /// Continuous in F at the boundary (F(L) = 0 from both sides).
        /// </summary>
        private static void CouplerLaw(float s, out float force, out float kEff, out float cEff)
        {
            float L     = ChainSolverConfig.CouplerSlackLimitMeters;
            float kHard = ChainSolverConfig.CouplerStiffnessHard;
            float cHard = ChainSolverConfig.CouplerDampingHard;

            float absS = s < 0f ? -s : s;
            if (absS <= L)
            {
                // Dead zone — coupler does nothing.
                force = 0f;
                kEff  = 0f;
                cEff  = 0f;
            }
            else
            {
                float sign = s < 0f ? -1f : 1f;
                force = kHard * (absS - L) * sign;
                kEff  = kHard;
                cEff  = cHard;
            }
        }

        /// <summary>
        /// Computes F_external_i · dt for every car, into the destination
        /// buffer. Per-car: brake, drag, grade. Per loco: traction summed
        /// into the loco's car index (MU contract).
        /// </summary>
        private static void ComputeExternalForcesScaled(ManagedConsist c, float dt, float[] outScaled)
        {
            int n = c.CarCount;

            float decelMax = ChainSolverConfig.BrakeForceMaxDecelMps2;
            float maxCyl   = ChainSolverConfig.CylinderMaxPsi;
            float dragK    = ChainSolverConfig.DragLinearPerKg;
            float g        = ChainSolverConfig.G;
            var graph      = Graph.Shared;
            var cyl        = c.CylinderPressurePsi;

            // Pass 1: per-car non-traction forces (brake, drag, grade).
            //
            // Brake force is now driven by *this car's* cylinder pressure,
            // which propagates from the lead loco via the brake-pipe field.
            // Cars far from the loco brake later because their pipe pressure
            // drops later — the wave through the train.

            for (int i = 0; i < n; i++)
            {
                var car = c.CarsArray[i];
                float v_i = c.Velocities[i];
                float m_i = c.Masses[i];

                float vSign = Mathf.Abs(v_i) < 1e-4f ? 0f : Mathf.Sign(v_i);
                float brakeFrac = (cyl.Length > i ? cyl[i] : 0f) / maxCyl;
                if (brakeFrac < 0f) brakeFrac = 0f;
                else if (brakeFrac > 1f) brakeFrac = 1f;
                float fBrakeRaw = -brakeFrac * m_i * decelMax * vSign;
                float maxBrakeMag = Mathf.Abs(v_i) * m_i / Mathf.Max(dt, 1e-4f);
                float fBrake = Mathf.Sign(fBrakeRaw) * Mathf.Min(Mathf.Abs(fBrakeRaw), maxBrakeMag);

                float fDrag = -dragK * m_i * v_i;

                float fGrade = 0f;
                if (graph != null)
                {
                    float gradePct = graph.GradeAtLocation(car.WheelBoundsF);
                    float gradeRad = gradePct * 0.01f;
                    fGrade = -m_i * g * gradeRad * c.OrientationSign;
                }

                outScaled[i] = (fBrake + fDrag + fGrade) * dt;
            }

            // Pass 2: traction at each loco (MU: shared throttle/reverser).
            float reverserSign = Mathf.Abs(c.Reverser) < 0.01f ? 0f : Mathf.Sign(c.Reverser);
            if (reverserSign != 0f && c.Throttle > 0f)
            {
                float thr = c.Throttle * reverserSign;
                for (int j = 0; j < c.LocoIndices.Length; j++)
                {
                    int idx = c.LocoIndices[j];
                    outScaled[idx] += thr * c.LocoTeNewtons[j] * dt;
                }
            }
        }

        private static void WriteCarPositions(ManagedConsist c, float dt)
        {
            var graph = Graph.Shared;
            if (graph == null) return;

            int n = c.CarCount;
            for (int i = 0; i < n; i++)
            {
                float ds = c.Velocities[i] * dt * c.OrientationSign;
                if (Mathf.Abs(ds) < 1e-7f) continue;

                var car = c.CarsArray[i];
                var newLoc = graph.LocationByMoving(car.WheelBoundsF, ds);
                car.PositionWheelBoundsFront(newLoc, graph, MovementInfo.Zero, update: true);
            }
        }

        private static void EnsureBuffers(int n)
        {
            if (_diag.Length < n)
            {
                int sz = Mathf.NextPowerOfTwo(n);
                _diag    = new float[sz];
                _rhs     = new float[sz];
                _cPrime  = new float[sz];
                _dPrime  = new float[sz];
                _beta    = new float[sz];     // sized N to spare; only N-1 used
                _fOld    = new float[sz];
            }
        }

        private static void LogState(ManagedConsist c)
        {
            int n = c.CarCount;
            int leadIdx = c.LeadLocoIndex >= 0 ? c.LeadLocoIndex : 0;
            float vLead = c.Velocities[leadIdx];
            float vRear = c.Velocities[n - 1];

            // Stretch summary: largest absolute, and how many couplers are
            // bottomed out (|stretch| > slack limit).
            float L = ChainSolverConfig.CouplerSlackLimitMeters;
            float maxStretch = 0f;
            int  hardCount = 0;
            for (int i = 0; i < c.Stretches.Length; i++)
            {
                float a = Mathf.Abs(c.Stretches[i]);
                if (a > maxStretch) maxStretch = a;
                if (a > L) hardCount++;
            }

            ExperimentEntry.Mod?.Logger?.Log(
                $"[solver] N={n} locos={c.LocoIndices.Length} thr={c.Throttle:F2} rev={c.Reverser:F2} tb={c.TrainBrake:F2} " +
                $"v_lead={vLead:F3} v_rear={vRear:F3} maxStretch={maxStretch*1000f:F1}mm bottomed={hardCount}/{c.Stretches.Length}");
        }
    }
}
