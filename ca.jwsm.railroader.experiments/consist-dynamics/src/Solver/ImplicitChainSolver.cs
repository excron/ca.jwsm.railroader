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
    /// Coupler model is dead-zone with hard walls: zero force inside the
    /// slack window, near-rigid spring outside. Per-coupler stiffness
    /// varies by current stretch, so the tridiagonal matrix has non-uniform
    /// off-diagonals — but the structure is unchanged and Thomas remains
    /// O(N) with no iteration.
    ///
    /// That's the whole "Featherstone for 1D" payload: the structure of
    /// the matrix gives the answer in one O(N) sweep, regardless of
    /// coupler stiffness or how nonlinear the regime transitions are.
    ///
    /// Loop structure (after fusion pass):
    ///   - One forward sweep that builds β/F_old, computes per-car external
    ///     forces, applies traction at loco indices, builds the matrix row,
    ///     and runs the Thomas forward elimination — all per car.
    ///   - One backward sweep that runs Thomas back-substitution, updates
    ///     velocities and stretches, and writes back per-car positions to
    ///     vanilla transforms — all per car.
    /// </summary>
    internal static class ImplicitChainSolver
    {
        // Per-tick scratch buffers, sized to the largest consist seen.
        // _rhs is no longer written after Thomas forward populates _dPrime,
        // but kept around as a working buffer during the forward sweep.
        private static float[] _diag    = new float[64];
        private static float[] _beta    = new float[64];
        private static float[] _rhs     = new float[64];
        private static float[] _cPrime  = new float[64];
        private static float[] _dPrime  = new float[64];
        private static float[] _fOld    = new float[64];

        private static float _nextLogTime;

        public static void Step(ManagedConsist consist, float dt)
        {
            int n = consist.CarCount;
            if (n == 0) return;

            EnsureBuffers(n);

            var v       = consist.Velocities;
            var m       = consist.Masses;
            var stretch = consist.Stretches;
            var cyl     = consist.CylinderPressurePsi;
            var cars    = consist.CarsArray;
            var locoIdx = consist.LocoIndices;
            var locoTe  = consist.LocoTeNewtons;
            var graph   = Graph.Shared;

            // Force-model constants (lifted out of the loop).
            float decelMax = ChainSolverConfig.BrakeForceMaxDecelMps2;
            float maxCyl   = ChainSolverConfig.CylinderMaxPsi;
            float dragK    = ChainSolverConfig.DragLinearPerKg;
            float gAcc     = ChainSolverConfig.G;
            float orient   = consist.OrientationSign;
            float invDt    = 1f / Mathf.Max(dt, 1e-4f);
            float dt2      = dt * dt;

            // Traction setup. thrFactor folds throttle × reverser_sign × dt
            // so the inner loop just multiplies by per-loco TE.
            float reverserSign = Mathf.Abs(consist.Reverser) < 0.01f ? 0f : Mathf.Sign(consist.Reverser);
            float thrFactor = (reverserSign != 0f && consist.Throttle > 0f)
                ? consist.Throttle * reverserSign * dt
                : 0f;
            int locoPtr = 0;
            int locoCount = locoIdx.Length;

            // ---- Forward pass: β + per-car forces + traction + matrix + Thomas forward ----
            //
            // Five loops fuse into one. Per-iteration body, in order:
            //   1. β[i] / F_old[i] for couplers (only if i < n-1)
            //   2. Per-car external forces (brake/drag/grade) → rhs_i
            //   3. Traction if this car is a loco (sorted-LocoIndices walker)
            //   4. Matrix row build (boundary/interior branches)
            //   5. Thomas forward elimination step
            //
            // Data dependencies satisfied because all of these walk forward
            // and prior-iteration values (β[i-1], _cPrime[i-1], _dPrime[i-1])
            // are exactly what we need.

            for (int i = 0; i < n; i++)
            {
                // ---- 1. Coupler β + F_old (only valid for couplers, 0..n-2) ----
                if (i < n - 1)
                {
                    CouplerLaw(stretch[i], out float fOldI, out float kEff, out float cEff);
                    _fOld[i] = fOldI;
                    _beta[i] = kEff * dt2 + cEff * dt;
                }

                // ---- 2. Per-car external forces (brake / drag / grade) ----
                var car = cars[i];
                float v_i = v[i];
                float m_i = m[i];

                float vSign = Mathf.Abs(v_i) < 1e-4f ? 0f : Mathf.Sign(v_i);
                float brakeFrac = cyl[i] / maxCyl;
                if (brakeFrac < 0f) brakeFrac = 0f;
                else if (brakeFrac > 1f) brakeFrac = 1f;
                float fBrakeRaw = -brakeFrac * m_i * decelMax * vSign;
                float maxBrakeMag = Mathf.Abs(v_i) * m_i * invDt;
                float fBrake = Mathf.Sign(fBrakeRaw) * Mathf.Min(Mathf.Abs(fBrakeRaw), maxBrakeMag);

                float fDrag = -dragK * m_i * v_i;

                float fGrade = 0f;
                if (graph != null)
                {
                    float gradePct = graph.GradeAtLocation(car.WheelBoundsF);
                    fGrade = -m_i * gAcc * gradePct * 0.01f * orient;
                }

                float rhs_i = (fBrake + fDrag + fGrade) * dt;

                // ---- 3. Traction at locos (MU: shared throttle × per-loco TE) ----
                // locoIdx is sorted ascending; a single walking pointer keeps
                // this O(N) total across the whole consist with no per-car
                // search.
                if (thrFactor != 0f && locoPtr < locoCount && locoIdx[locoPtr] == i)
                {
                    rhs_i += thrFactor * locoTe[locoPtr];
                    locoPtr++;
                }

                // ---- 4. Matrix row + coupler explicit-force terms ----
                float diag_i;
                if (n == 1)
                {
                    diag_i = m_i;
                    // No couplers when there's only one car.
                }
                else if (i == 0)
                {
                    // Front car: only a right coupler β[0].
                    float betaR = _beta[0];
                    diag_i = m_i + betaR;
                    rhs_i += -_fOld[0] * dt + betaR * (v[1] - v_i);
                }
                else if (i == n - 1)
                {
                    // Rear car: only a left coupler β[n-2].
                    float betaL = _beta[i - 1];
                    diag_i = m_i + betaL;
                    rhs_i += +_fOld[i - 1] * dt + betaL * (v[i - 1] - v_i);
                }
                else
                {
                    // Interior car: both couplers contribute.
                    float betaL = _beta[i - 1];
                    float betaR = _beta[i];
                    diag_i = m_i + betaL + betaR;
                    rhs_i += (_fOld[i - 1] - _fOld[i]) * dt
                          + betaL * (v[i - 1] - v_i)
                          + betaR * (v[i + 1] - v_i);
                }

                _diag[i] = diag_i;
                _rhs[i]  = rhs_i;

                // ---- 5. Thomas forward elimination step ----
                // Sub- and super-diagonals are A[i,i+1] = A[i+1,i] = -β[i],
                // stored implicitly via _beta[].
                if (i == 0)
                {
                    _cPrime[0] = (n > 1) ? (-_beta[0] / diag_i) : 0f;
                    _dPrime[0] = rhs_i / diag_i;
                }
                else
                {
                    float subDiag = -_beta[i - 1];
                    float denom = diag_i - subDiag * _cPrime[i - 1];
                    if (i < n - 1)
                        _cPrime[i] = -_beta[i] / denom;
                    _dPrime[i] = (rhs_i - subDiag * _dPrime[i - 1]) / denom;
                }
            }

            // ---- Backward pass: Thomas back-sub + v + stretch + position writeback ----
            //
            // Three N-sized operations fuse cleanly because all data
            // dependencies cooperate:
            //   - Thomas back-sub naturally walks n-1 → 0
            //   - Velocity update v[i] += dv_i is order-independent
            //   - Stretch update reads v[i] (just updated) and v[i+1]
            //     (updated in the previous backward iter) — both current
            //   - Position writeback only needs v[i] (just updated) and the
            //     car's current WheelBoundsF — purely local
            // dv carried in a local; _rhs[] no longer needs to be written.

            // Tail car (i == n-1): no upstream dep, no stretch entry, but
            // still wants velocity update and writeback.
            {
                int i = n - 1;
                float dv = _dPrime[i];
                v[i] += dv;
                WriteCarPosition(graph, cars[i], v[i] * dt * orient);
            }

            float dvNext = _dPrime[n - 1];
            for (int i = n - 2; i >= 0; i--)
            {
                float dvCur = _dPrime[i] - _cPrime[i] * dvNext;
                v[i] += dvCur;
                stretch[i] += (v[i] - v[i + 1]) * dt;
                WriteCarPosition(graph, cars[i], v[i] * dt * orient);
                dvNext = dvCur;
            }

            // ---- Diagnostics, rate-limited ----
            if (Time.realtimeSinceStartup >= _nextLogTime)
            {
                _nextLogTime = Time.realtimeSinceStartup + 1f;
                LogState(consist);
            }
        }

        /// <summary>
        /// Tiny inline-able helper for the per-car position writeback.
        /// Skips no-op if ds is essentially zero or graph is unavailable.
        /// </summary>
        private static void WriteCarPosition(Graph graph, Model.Car car, float ds)
        {
            if (graph == null) return;
            if (Mathf.Abs(ds) < 1e-7f) return;
            var newLoc = graph.LocationByMoving(car.WheelBoundsF, ds);
            car.PositionWheelBoundsFront(newLoc, graph, MovementInfo.Zero, update: true);
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

        private static void EnsureBuffers(int n)
        {
            if (_diag.Length < n)
            {
                int sz = Mathf.NextPowerOfTwo(n);
                _diag    = new float[sz];
                _rhs     = new float[sz];
                _cPrime  = new float[sz];
                _dPrime  = new float[sz];
                _beta    = new float[sz];
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
