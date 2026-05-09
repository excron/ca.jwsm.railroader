using System;
using Game.Messages;
using KeyValue.Runtime;
using Ca.Jwsm.Railroader.Experiments.ConsistDynamics.State;

namespace Ca.Jwsm.Railroader.Experiments.ConsistDynamics.Input
{
    /// <summary>
    /// Subscribes to KVO changes on the lead loco for HUD-driven control
    /// inputs. Vanilla HUD writes throttle/brake/reverser into the same
    /// CarControlProperties → KeyValueObject substrate; we observe and
    /// cache into ManagedConsist for the solver to read.
    /// </summary>
    internal sealed class ControlObserver : IDisposable
    {
        private readonly IDisposable _throttleSub;
        private readonly IDisposable _trainBrakeSub;
        private readonly IDisposable _reverserSub;

        public ControlObserver(ManagedConsist consist)
        {
            var kvo = consist.LeadLoco.KeyValueObject;

            _throttleSub = kvo.Observe(
                PropertyChange.KeyForControl(PropertyChange.Control.Throttle),
                v => { consist.Throttle = v.FloatValue; LogChange("throttle", v.FloatValue); });

            _trainBrakeSub = kvo.Observe(
                PropertyChange.KeyForControl(PropertyChange.Control.TrainBrake),
                v => { consist.TrainBrake = v.FloatValue; LogChange("trainBrake", v.FloatValue); });

            _reverserSub = kvo.Observe(
                PropertyChange.KeyForControl(PropertyChange.Control.Reverser),
                v => { consist.Reverser = v.FloatValue; LogChange("reverser", v.FloatValue); });
        }

        private static void LogChange(string control, float value)
        {
            ExperimentEntry.Mod?.Logger?.Log($"[input] {control}={value:F3}");
        }

        public void Dispose()
        {
            _throttleSub?.Dispose();
            _trainBrakeSub?.Dispose();
            _reverserSub?.Dispose();
        }
    }
}
