using System.Collections.Generic;
using System.Reflection;
using Model.Physics;
using UnityEngine;
using Ca.Jwsm.Railroader.Experiments.ConsistDynamics.Input;
using Ca.Jwsm.Railroader.Experiments.ConsistDynamics.Solver;
using Ca.Jwsm.Railroader.Experiments.ConsistDynamics.State;

namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics.Driver
{
    /// <summary>
    /// Tick driver. Invoked once per FixedUpdate from the postfix on
    /// TrainController.FixedUpdate. Runs after the suppressed vanilla
    /// body, so the IntegrationSetManager state is whatever it was
    /// last frame (no vanilla mutations between frames).
    /// </summary>
    internal static class ConsistDriver
    {
        private static readonly Dictionary<IntegrationSet, ManagedConsist> _consists
            = new Dictionary<IntegrationSet, ManagedConsist>();

        private static readonly Dictionary<IntegrationSet, ControlObserver> _observers
            = new Dictionary<IntegrationSet, ControlObserver>();

        // Cache the FieldInfo for TrainController._integrationSets once.
        // Harmony.Traverse allocates per call; FieldInfo.GetValue does not.
        private static readonly FieldInfo _integrationSetsField = typeof(TrainController)
            .GetField("_integrationSets", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Tick(TrainController tc, float dt)
        {
            if (tc == null || _integrationSetsField == null) return;

            var manager = (IntegrationSetManager)_integrationSetsField.GetValue(tc);
            if (manager == null) return;

            SyncRegistry(manager);

            float vEps = ChainSolverConfig.AtRestVelocityEps;
            float iEps = ChainSolverConfig.InputEps;

            foreach (var consist in _consists.Values)
            {
                if (consist.LeadLoco == null) continue;

                // Combined rest check — single walk over the per-car arrays
                // determines whether each subsystem can sleep this tick.
                ScanRestState(consist, vEps, out bool airQuiescent, out bool chainAtRest, out float maxCyl);

                // Air sleep: skip the diffusion solve + cylinder ODE +
                // vanilla writeback when there's no input demand and the
                // pipe field is at charge with cylinders at zero. Anything
                // that could change the air state (TrainBrake KVO, a pipe
                // value out of equilibrium, a cylinder above zero) flips
                // airQuiescent off and the solver runs as normal.
                if (!airQuiescent)
                    AirPipeSolver.Step(consist, dt);

                // Chain sleep: skip the tridiagonal solve when all
                // velocities are below ε, no throttle/brake input is being
                // demanded, and no cylinder pressure exists that could
                // produce brake force. (Brake force at rest is zero anyway
                // because sign(v)≈0, but a high cylinder during a coasting
                // brake-application transient is the edge case we care about.)
                bool noChainInput = Mathf.Abs(consist.Throttle) < iEps
                                  && consist.TrainBrake < iEps
                                  && maxCyl < 1f;
                if (chainAtRest && noChainInput) continue;

                ImplicitChainSolver.Step(consist, dt);
            }
        }

        /// <summary>
        /// Single-pass scan of per-car arrays that returns both the chain
        /// at-rest state, the air quiescent state, and the max cylinder
        /// pressure (used by the chain gate). Replaces three separate walks
        /// with one over the same data.
        /// </summary>
        private static void ScanRestState(
            ManagedConsist c, float vEps,
            out bool airQuiescent, out bool chainAtRest, out float maxCyl)
        {
            // TrainBrake is a fast pre-check for air quiescence — if the
            // player is currently demanding brakes, we don't even bother
            // walking the arrays.
            bool airInputQuiet = c.TrainBrake < ChainSolverConfig.InputEps;

            float charge = ChainSolverConfig.ChargePressurePsi;
            const float PipeEps = 0.1f;   // psi — within this much of charge counts as "at charge"
            const float CylEps  = 0.1f;   // psi — within this much of zero counts as "released"

            var v    = c.Velocities;
            var cyl  = c.CylinderPressurePsi;
            var pipe = c.PipePressurePsi;

            bool airAtEquilibrium = airInputQuiet;
            bool atRest = true;
            float cylMax = 0f;

            int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                float vi = v[i];
                float vAbs = vi < 0f ? -vi : vi;
                if (vAbs > vEps) atRest = false;

                float cylI = cyl[i];
                if (cylI > cylMax) cylMax = cylI;
                if (cylI > CylEps) airAtEquilibrium = false;

                float pipeDrop = charge - pipe[i];
                if (pipeDrop > PipeEps || pipeDrop < -PipeEps) airAtEquilibrium = false;
            }

            airQuiescent = airAtEquilibrium;
            chainAtRest  = atRest;
            maxCyl       = cylMax;
        }

        private static void SyncRegistry(IntegrationSetManager manager)
        {
            var seen = HashSetPool.Rent();
            try
            {
                foreach (var set in manager)
                {
                    seen.Add(set);
                    if (!_consists.ContainsKey(set))
                    {
                        var managed = new ManagedConsist(set);
                        managed.Refresh();
                        _consists[set] = managed;

                        if (managed.LeadLoco != null)
                        {
                            var observer = new ControlObserver(managed);
                            _observers[set] = observer;
                        }
                    }
                }

                // Drop sets that vanished. Iterate a snapshot — we mutate inside.
                using (var stale = ListPool.Rent<IntegrationSet>())
                {
                    foreach (var key in _consists.Keys)
                        if (!seen.Contains(key)) stale.Value.Add(key);
                    foreach (var key in stale.Value)
                    {
                        if (_observers.TryGetValue(key, out var obs))
                        {
                            obs.Dispose();
                            _observers.Remove(key);
                        }
                        _consists.Remove(key);
                    }
                }
            }
            finally
            {
                HashSetPool.Return(seen);
            }
        }

        // -------------------------------------------------------------------
        // Trivial pools — avoid GC churn from per-tick allocations. Phase 1
        // doesn't need fancy infra; this gets us off the GC's radar.
        // -------------------------------------------------------------------

        private static class HashSetPool
        {
            private static readonly Stack<HashSet<IntegrationSet>> _pool = new Stack<HashSet<IntegrationSet>>();
            public static HashSet<IntegrationSet> Rent()
                => _pool.Count > 0 ? _pool.Pop() : new HashSet<IntegrationSet>();
            public static void Return(HashSet<IntegrationSet> set)
            {
                set.Clear();
                _pool.Push(set);
            }
        }

        private readonly struct ListLease<T> : System.IDisposable
        {
            public readonly List<T> Value;
            private readonly Stack<List<T>> _pool;
            public ListLease(List<T> v, Stack<List<T>> p) { Value = v; _pool = p; }
            public void Dispose() { Value.Clear(); _pool.Push(Value); }
        }

        private static class ListPool
        {
            private static readonly Dictionary<System.Type, object> _pools
                = new Dictionary<System.Type, object>();
            public static ListLease<T> Rent<T>()
            {
                if (!_pools.TryGetValue(typeof(T), out var raw))
                {
                    raw = new Stack<List<T>>();
                    _pools[typeof(T)] = raw;
                }
                var stack = (Stack<List<T>>)raw;
                return new ListLease<T>(stack.Count > 0 ? stack.Pop() : new List<T>(), stack);
            }
        }
    }
}
