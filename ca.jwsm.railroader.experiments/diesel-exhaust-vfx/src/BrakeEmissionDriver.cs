using System.Collections.Generic;
using RollingStock;
using UnityEngine;

namespace Ca.Jwsm.Railroader.Experiments.DieselExhaustVfx
{
    /// <summary>
    /// Drives _EmissionColor on overlay materials (created by
    /// WheelsetBrakeEmissionPatch as additive child renderers parented to
    /// brake-shoe and wheel meshes). The original Railroader materials are
    /// not touched — overlays additively render glow on top.
    ///
    /// Three overlay classes:
    /// - shoes (hot, with flicker)
    /// - wheel discs (face/inboard, cooler — heat soaked from rim)
    /// - wheel treads (the rail-contact band, hot — actual heating surface)
    /// </summary>
    public class BrakeEmissionDriver : MonoBehaviour
    {
        public Wheelset Wheelset;
        public List<Material> ShoeOverlayMaterials = new List<Material>();
        public List<Material> WheelDiscOverlayMaterials = new List<Material>();
        public List<Material> WheelTreadOverlayMaterials = new List<Material>();

        // Hot brake-shoe color before HDR multiplier — orange-red.
        private static readonly Color ShoeBaseColor = new Color(1.0f, 0.25f, 0.05f);

        // Wheel face base color — slightly redder/cooler to read as soaked heat.
        private static readonly Color WheelBaseColor = new Color(0.85f, 0.15f, 0.04f);

        // Wheel rim/tread color — hotter than the face since it's the contact surface.
        private static readonly Color WheelRimBaseColor = new Color(1.0f, 0.30f, 0.06f);

        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private float _shoeCurrent;
        private float _wheelFaceCurrent;
        private float _wheelRimCurrent;

        private void Update()
        {
            if (Wheelset == null) return;
            var settings = ExperimentEntry.Settings;
            if (settings == null) return;

            bool applied = settings.ForceAlwaysOn || Wheelset.BrakeApplied;

            float shoeTarget = applied
                ? settings.ShoeIntensity * Random.Range(settings.FlickerLow, settings.FlickerHigh)
                : 0f;
            float wheelFaceTarget = applied ? settings.WheelIntensity : 0f;
            float wheelRimTarget = applied ? settings.WheelRimIntensity : 0f;

            _shoeCurrent = Mathf.Lerp(_shoeCurrent, shoeTarget, Time.deltaTime * settings.ShoeChaseRate);
            _wheelFaceCurrent = Mathf.Lerp(_wheelFaceCurrent, wheelFaceTarget, Time.deltaTime * settings.WheelChaseRate);
            _wheelRimCurrent = Mathf.Lerp(_wheelRimCurrent, wheelRimTarget, Time.deltaTime * settings.WheelChaseRate);

            ApplyEmission(ShoeOverlayMaterials, ShoeBaseColor * _shoeCurrent);
            ApplyEmission(WheelDiscOverlayMaterials, WheelBaseColor * _wheelFaceCurrent);
            ApplyEmission(WheelTreadOverlayMaterials, WheelRimBaseColor * _wheelRimCurrent);
        }

        private static void ApplyEmission(List<Material> materials, Color emission)
        {
            for (int i = 0; i < materials.Count; i++)
            {
                var m = materials[i];
                if (m != null) m.SetColor(EmissionColorId, emission);
            }
        }
    }
}
