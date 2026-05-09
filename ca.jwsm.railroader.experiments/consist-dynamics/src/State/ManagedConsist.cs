using System.Collections.Generic;
using Model;
using Model.Physics;

namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics.State
{
    /// <summary>
    /// Per-consist state. Phase 2: per-car arc-length state with per-coupler
    /// stretch. Vanilla's IntegrationSet is read only for topology (which
    /// cars belong together); we never touch Element or its physics state.
    ///
    /// Arrays are parallel-indexed over Cars: Velocities[i], Masses[i].
    /// Stretches has length Cars.Count - 1 (one entry per coupler).
    /// </summary>
    internal sealed class ManagedConsist
    {
        public IntegrationSet VanillaSet { get; }
        public IReadOnlyList<Car> Cars => _cars;

        // Direct-array view for hot loops in solvers. CarsArray[i] is a
        // direct array indexer (no interface dispatch) — meaningful for
        // 300+ car consists where the solvers walk the list 4-6 times
        // per tick. Length is CarCount; do NOT iterate beyond that.
        // Refreshed alongside _cars in Refresh().
        public Car[] CarsArray { get; private set; } = System.Array.Empty<Car>();
        public int CarCount    => _cars.Count;

        // Lead loco — the one we observe for HUD inputs. In an MU group
        // vanilla syncs control properties across all locos, so reading
        // any one of them gives the right value; we pick the first found.
        public BaseLocomotive LeadLoco { get; private set; }
        public int LeadLocoIndex { get; private set; } = -1;

        // All locos in the consist. MU is implicit — every loco contributes
        // its own RatedTractiveEffort under the shared HUD throttle/reverser.
        // Indices are positions in Cars[]; LocoTeNewtons[j] is the TE for
        // the loco at LocoIndices[j].
        public int[]   LocoIndices    { get; private set; } = System.Array.Empty<int>();
        public float[] LocoTeNewtons  { get; private set; } = System.Array.Empty<float>();

        public float[] Velocities { get; private set; } = System.Array.Empty<float>();
        public float[] Masses     { get; private set; } = System.Array.Empty<float>();
        public float[] Stretches  { get; private set; } = System.Array.Empty<float>();

        // Air system state (phase 3a). PipePressurePsi is the brake-pipe
        // pressure field along the consist; CylinderPressurePsi is the
        // per-car brake cylinder pressure (lagged from pipe via local AB
        // valve dynamics). Both initialized to charged-released state on
        // adoption.
        public float[] PipePressurePsi     { get; private set; } = System.Array.Empty<float>();
        public float[] CylinderPressurePsi { get; private set; } = System.Array.Empty<float>();

        // HUD inputs (set by ControlObserver KVO callbacks).
        public float Throttle;
        public float TrainBrake;
        public float Reverser;

        // Reverser sign for converting "consist-forward velocity" to a signed
        // advance along each car's WheelBoundsF endOrientation. Determined
        // once per consist on adoption from the lead loco's orientation.
        // Phase 2 simplification: assume all cars share the lead's orientation.
        public float OrientationSign = 1f;

        private readonly List<Car> _cars = new List<Car>();

        public ManagedConsist(IntegrationSet vanillaSet)
        {
            VanillaSet = vanillaSet;
        }

        public void Refresh()
        {
            _cars.Clear();
            LeadLoco = null;
            LeadLocoIndex = -1;

            const float LbToKg  = 0.453592f;
            const float LbfToN  = 4.4482216f;

            var locoIdx = new List<int>();
            var locoTe  = new List<float>();

            int idx = 0;
            foreach (var car in VanillaSet.Cars)
            {
                _cars.Add(car);
                if (car is BaseLocomotive loco)
                {
                    locoIdx.Add(idx);
                    locoTe.Add(loco.RatedTractiveEffort * LbfToN);
                    if (LeadLoco == null)
                    {
                        LeadLoco = loco;
                        LeadLocoIndex = idx;
                    }
                }
                idx++;
            }

            int n = _cars.Count;
            Velocities          = new float[n];
            Masses              = new float[n];
            Stretches           = (n > 1) ? new float[n - 1] : System.Array.Empty<float>();
            PipePressurePsi     = new float[n];
            CylinderPressurePsi = new float[n];
            CarsArray           = _cars.ToArray();   // direct-array view for solver hot paths
            LocoIndices   = locoIdx.ToArray();
            LocoTeNewtons = locoTe.ToArray();

            for (int i = 0; i < n; i++)
            {
                Masses[i] = _cars[i].Weight * LbToKg;
                // Start charged & released — typical yard-spawn condition.
                PipePressurePsi[i]     = Solver.ChainSolverConfig.ChargePressurePsi;
                CylinderPressurePsi[i] = 0f;
            }

            float totalTe = 0f;
            for (int j = 0; j < LocoTeNewtons.Length; j++) totalTe += LocoTeNewtons[j];

            ExperimentEntry.Mod?.Logger?.Log(
                $"[adopt] consist set={VanillaSet.GetHashCode():X} cars={n} " +
                $"locos={LocoIndices.Length} lead={(LeadLoco != null ? LeadLoco.DisplayName : "<none>")}@{LeadLocoIndex} " +
                $"totalMass={SumMass():F0}kg sumTE={totalTe:F0}N");
        }

        public float SumMass()
        {
            float m = 0f;
            for (int i = 0; i < Masses.Length; i++) m += Masses[i];
            return m;
        }
    }
}
