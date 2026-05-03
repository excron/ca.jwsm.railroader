using UnityEngine;
using UnityEngine.VFX;

namespace Ca.Jwsm.Railroader.Experiments.DieselExhaustVfx
{
    /// <summary>
    /// Drives a Light's intensity with smoothed random flicker, evoking
    /// a fire/exhaust point source. Also keeps its local position synced
    /// to the parent VisualEffect's PositionOffset exposed property, so
    /// the light sits at the actual particle emission point (typically
    /// the locomotive's stack), not at the GameObject root.
    /// Attached at runtime by the Harmony postfix on
    /// DieselExhaustParticleController.OnEnable.
    /// </summary>
    public class ExhaustFlicker : MonoBehaviour
    {
        // Current and target intensities for smooth interpolation.
        private float _currentIntensity;
        private float _targetIntensity;

        // How often we pick a new flicker target (in seconds).
        private const float FlickerInterval = 0.05f;

        // Range multiplier applied to base intensity for flicker (low, high).
        private const float FlickerLow = 0.4f;
        private const float FlickerHigh = 1.4f;

        // How fast the current intensity chases the target. Higher = snappier.
        private const float ChaseRate = 25f;

        // Cached property name id for fast PositionOffset reads.
        private static readonly int PositionOffsetId = Shader.PropertyToID("PositionOffset");

        private Light _light;
        private VisualEffect _vfx;
        private float _timer;

        private void Awake()
        {
            _light = GetComponent<Light>();
            _vfx = GetComponentInParent<VisualEffect>();
        }

        private void Update()
        {
            // Sync localPosition to PositionOffset so the light sits at the
            // actual emission point. Cheap (one Vector3 read per frame).
            if (_vfx != null && _vfx.HasVector3(PositionOffsetId))
            {
                transform.localPosition = _vfx.GetVector3(PositionOffsetId);
            }

            // Live-read base intensity from settings so the UMM slider
            // (off → bright) takes effect without restart.
            float baseIntensity = ExperimentEntry.Settings != null
                ? ExperimentEntry.Settings.ExhaustIntensity
                : 0f;

            _timer += Time.deltaTime;
            if (_timer >= FlickerInterval)
            {
                _timer = 0f;
                _targetIntensity = baseIntensity * Random.Range(FlickerLow, FlickerHigh);
            }

            _currentIntensity = Mathf.Lerp(
                _currentIntensity,
                _targetIntensity,
                Time.deltaTime * ChaseRate);

            _light.intensity = _currentIntensity;
            // Hard off when slider is at zero so we don't pay any draw cost.
            _light.enabled = baseIntensity > 0.001f && _currentIntensity > 0.001f;
        }
    }
}
