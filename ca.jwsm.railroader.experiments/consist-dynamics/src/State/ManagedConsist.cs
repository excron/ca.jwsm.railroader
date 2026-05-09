using System.Collections.Generic;
using Model;
using Model.Physics;

namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics.State
{
    /// <summary>
    /// Our per-consist state. Owns kinematic and force values for the
    /// rigid-consist phase 1 model. Vanilla's IntegrationSet is never
    /// read — it only tells us "which cars belong together."
    ///
    /// Phase 1: single-DOF (one s, one v for the whole consist).
    /// Phase 2+: per-car arc-length state lives here too.
    /// </summary>
    internal sealed class ManagedConsist
    {
        public IntegrationSet VanillaSet { get; }
        public IReadOnlyList<Car> Cars => _cars;
        public BaseLocomotive LeadLoco { get; private set; }

        // Single-DOF kinematics (phase 1). v is signed in the
        // direction-of-travel of the lead loco.
        public float Velocity;          // m/s
        public float TotalMassKg;       // sum of Car.Weight (short tons → kg)
        public float RatedTeNewtons;    // lead loco's rated TE in newtons

        // Cached HUD inputs (updated by ControlObserver KVO subscriptions).
        public float Throttle;          // 0..1
        public float TrainBrake;        // 0..1
        public float Reverser;          // -1, 0, +1

        private readonly List<Car> _cars = new List<Car>();

        public ManagedConsist(IntegrationSet vanillaSet)
        {
            VanillaSet = vanillaSet;
        }

        /// <summary>
        /// Refresh the car list and lead-loco identification from the
        /// vanilla set. Cheap; called when topology changes (couple/decouple).
        /// </summary>
        public void Refresh()
        {
            _cars.Clear();
            LeadLoco = null;
            foreach (var car in VanillaSet.Cars)
            {
                _cars.Add(car);
                if (LeadLoco == null && car is BaseLocomotive loco)
                    LeadLoco = loco;
            }

            RecomputeMassAndTe();

            ExperimentEntry.Mod?.Logger?.Log(
                $"[adopt] consist set={VanillaSet.GetHashCode():X} cars={_cars.Count} " +
                $"lead={(LeadLoco != null ? LeadLoco.DisplayName : "<none>")} " +
                $"mass={TotalMassKg:F0}kg TE={RatedTeNewtons:F0}N");
        }

        private void RecomputeMassAndTe()
        {
            // Car.Weight is in **pounds** (despite the survey doc claiming
            // short tons — survey is wrong; vanilla code at
            // IntegrationSet.cs:393/430 multiplies Weight by 0.453592 to
            // get kg, and Car.GravityForce divides Weight by 2000 to get
            // short tons).
            const float LbToKg = 0.453592f;
            float totalLb = 0f;
            foreach (var c in _cars) totalLb += c.Weight;
            TotalMassKg = totalLb * LbToKg;

            // RatedTractiveEffort is in pounds-force.
            const float LbfToN = 4.4482216f;
            RatedTeNewtons = LeadLoco != null
                ? LeadLoco.RatedTractiveEffort * LbfToN
                : 0f;
        }
    }
}
