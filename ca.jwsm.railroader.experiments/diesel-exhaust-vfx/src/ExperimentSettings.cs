using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.DieselExhaustVfx
{
    /// <summary>
    /// UMM-backed live-tunable settings for the diesel exhaust experiment.
    /// All values are read every frame by their consumers, so tweaks take
    /// effect immediately without restarting the game.
    /// </summary>
    public class ExperimentSettings : UnityModManager.ModSettings, IDrawable
    {
        // --- Exhaust point light ---

        [Draw("Exhaust point light intensity (0 = off)", Min = 0f, Max = 10f, Type = DrawType.Slider, Precision = 2)]
        public float ExhaustIntensity = 3f;

        // --- Brake shoe / wheel emission ---

        [Draw("Shoe emission intensity (HDR)", Min = 0f, Max = 20f, Type = DrawType.Slider, Precision = 1)]
        public float ShoeIntensity = 8f;

        [Draw("Wheel face emission intensity (HDR)", Min = 0f, Max = 10f, Type = DrawType.Slider, Precision = 2)]
        public float WheelIntensity = 0.5f;

        [Draw("Wheel rim emission intensity (HDR)", Min = 0f, Max = 20f, Type = DrawType.Slider, Precision = 1)]
        public float WheelRimIntensity = 6f;

        [Draw("Shoe chase rate", Min = 0.1f, Max = 20f, Type = DrawType.Slider, Precision = 1)]
        public float ShoeChaseRate = 6f;

        [Draw("Wheel chase rate (heat soak)", Min = 0.05f, Max = 10f, Type = DrawType.Slider, Precision = 2)]
        public float WheelChaseRate = 1.5f;

        [Draw("Shoe flicker low", Min = 0f, Max = 1f, Type = DrawType.Slider, Precision = 2)]
        public float FlickerLow = 0.75f;

        [Draw("Shoe flicker high", Min = 1f, Max = 2f, Type = DrawType.Slider, Precision = 2)]
        public float FlickerHigh = 1.15f;

        [Draw("Force emission always on (bypass BrakeApplied)")]
        public bool ForceAlwaysOn = false;

        [Draw("Log Wheelset hierarchy on first attach")]
        public bool LogDiscovery = true;

        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);

        public void OnChange() { }
    }
}
