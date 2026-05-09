using System.Collections.Generic;
using HarmonyLib;
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

        public static void Tick(TrainController tc, float dt)
        {
            if (tc == null) return;

            var manager = Traverse.Create(tc)
                .Field("_integrationSets")
                .GetValue<IntegrationSetManager>();
            if (manager == null) return;

            SyncRegistry(manager);

            float vEps = ChainSolverConfig.AtRestVelocityEps;
            float iEps = ChainSolverConfig.InputEps;

            foreach (var consist in _consists.Values)
            {
                if (consist.LeadLoco == null) continue;

                // Air always ticks — pipe pressure can change while the
                // consist is at rest (charging up after spawn, brake set
                // by player while stopped, etc.). Cheap relative to chain.
                AirPipeSolver.Step(consist, dt);

                // Chain solver gated by motion + input. We use cylinder
                // pressure as a "non-rest" signal so a stopped consist
                // with brakes applied still gets force evaluated correctly.
                bool atRest = MaxAbsVelocity(consist) < vEps;
                bool noInput = Mathf.Abs(consist.Throttle) < iEps
                            && consist.TrainBrake < iEps
                            && MaxCylinderPressure(consist) < 1f;
                if (atRest && noInput) continue;

                ImplicitChainSolver.Step(consist, dt);
            }
        }

        private static float MaxCylinderPressure(ManagedConsist c)
        {
            float max = 0f;
            var p = c.CylinderPressurePsi;
            for (int i = 0; i < p.Length; i++) if (p[i] > max) max = p[i];
            return max;
        }

        private static float MaxAbsVelocity(ManagedConsist c)
        {
            float max = 0f;
            var v = c.Velocities;
            for (int i = 0; i < v.Length; i++)
            {
                float a = v[i] < 0f ? -v[i] : v[i];
                if (a > max) max = a;
            }
            return max;
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
