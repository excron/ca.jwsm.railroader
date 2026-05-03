using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RollingStock;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ca.Jwsm.Railroader.Experiments.DieselExhaustVfx
{
    /// <summary>
    /// Postfix on Wheelset.OnEnable that locates brake-shoe renderers by
    /// spatial proximity to the Wheelset's wheel transforms, then creates a
    /// child overlay MeshRenderer per shoe sharing the parent's mesh. Each
    /// overlay uses a tiny unlit additive shader driven by BrakeEmissionDriver
    /// — emission only, no shader swap on the original material, so we don't
    /// touch Railroader's car-shader render pipeline at all.
    ///
    /// Why proximity, not names: the Co71 truck's meshes are all named
    /// "Flexcoil_NN_LOD0" — there's no "wheel" or "shoe" string to match.
    /// We use the internal Wheelset.wheels list as the source of truth for
    /// wheel hub positions and bracket renderers around them.
    /// </summary>
    [HarmonyPatch(typeof(Wheelset), "OnEnable")]
    public static class WheelsetBrakeEmissionPatch
    {
        private static readonly FieldInfo WheelsField =
            AccessTools.Field(typeof(Wheelset), "wheels");

        private const string DriverChildName = "BrakeEmissionDriver";
        private const string OverlayChildName = "BrakeEmissionOverlay";

        // 1.0m catches actual shoes despite their lateral offset from the
        // wheel hub. Tighter radii (0.7m) lose the shoes entirely.
        // Some lever-arm bleed at this radius is acceptable.
        private const float ShoeMatchRadius = 1.0f;

        private static bool _loggedOnce;

        [HarmonyPostfix]
        public static void Postfix(Wheelset __instance)
        {
            if (__instance == null) return;
            if (__instance.transform.Find(DriverChildName) != null) return;

            var wheelTransforms = WheelsField?.GetValue(__instance) as List<Transform>;
            if (wheelTransforms == null || wheelTransforms.Count == 0) return;

            var allRenderers = __instance.GetComponentsInChildren<Renderer>(true);
            var shoeRenderers = new List<Renderer>();

            foreach (var r in allRenderers)
            {
                if (r == null) continue;
                var path = GetPath(r.transform).ToLowerInvariant();
                if (!path.Contains("brakerig")) continue;

                // Brake shoes are children of Bone.NNN transforms (the moving
                // bone-driven holders that swing in against the wheel). Static
                // parts of the rigging — rods, beams, levers — are direct
                // children of brakerig_N transforms instead. Filter to bone-
                // parented renderers only to drop the rod/lever bleed.
                var parent = r.transform.parent;
                if (parent == null || !parent.name.StartsWith("Bone.")) continue;

                float nearest = NearestWheelDistance(r.bounds.center, wheelTransforms);
                if (nearest < ShoeMatchRadius)
                    shoeRenderers.Add(r);
            }

            // Wheel renderers: union of (renderers from Wheelset.wheels list)
            // and (any renderer containing the WheelTread material on any
            // submesh slot). The wheel asset turns out to be a single mesh
            // with TWO submeshes — disc on slot 0, tread on slot 1 — so we
            // grab the renderer once and split on the material side later.
            var wheelRenderers = new List<Renderer>();
            var wheelRendererSet = new HashSet<Renderer>();

            foreach (var t in wheelTransforms)
            {
                if (t == null) continue;
                var r = t.GetComponent<Renderer>();
                if (r != null && wheelRendererSet.Add(r)) wheelRenderers.Add(r);
            }

            foreach (var r in allRenderers)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    if (m.name.IndexOf("WheelTread", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (wheelRendererSet.Add(r)) wheelRenderers.Add(r);
                        break;
                    }
                }
            }

            // Build overlays. Three classes: shoes, wheel discs (face), and
            // wheel treads (rim — actual rail-contact geometry).
            var shoeMaterials = new List<Material>();
            var wheelDiscMaterials = new List<Material>();
            var wheelTreadMaterials = new List<Material>();
            int shoeOverlays = 0;
            int wheelDiscOverlays = 0;
            int wheelTreadOverlays = 0;

            if (ExperimentEntry.EmissionOverlayShader != null)
            {
                foreach (var r in shoeRenderers)
                {
                    var newMats = TryCreateOverlay(r);
                    if (newMats == null) continue;
                    foreach (var m in newMats) shoeMaterials.Add(m);
                    shoeOverlays++;
                }
                foreach (var r in wheelRenderers)
                {
                    var newMats = TryCreateOverlay(r);
                    if (newMats == null) continue;
                    var parentMats = r.sharedMaterials;
                    for (int i = 0; i < newMats.Length; i++)
                    {
                        var pm = i < parentMats.Length ? parentMats[i] : null;
                        bool isTread = pm != null && pm.name.IndexOf("WheelTread", System.StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isTread) { wheelTreadMaterials.Add(newMats[i]); wheelTreadOverlays++; }
                        else { wheelDiscMaterials.Add(newMats[i]); wheelDiscOverlays++; }
                    }
                }
            }

            if (ExperimentEntry.Settings != null && ExperimentEntry.Settings.LogDiscovery && !_loggedOnce)
            {
                _loggedOnce = true;
                LogDiscovery(__instance, wheelTransforms, allRenderers,
                    shoeRenderers, wheelRenderers,
                    shoeOverlays, wheelDiscOverlays, wheelTreadOverlays);
            }

            if (shoeMaterials.Count == 0 && wheelDiscMaterials.Count == 0 && wheelTreadMaterials.Count == 0) return;

            // Deactivate before AddComponent so the driver's Awake doesn't
            // fire on an uninitialized instance.
            var driverGo = new GameObject(DriverChildName);
            driverGo.SetActive(false);
            var driver = driverGo.AddComponent<BrakeEmissionDriver>();
            driver.Wheelset = __instance;
            driver.ShoeOverlayMaterials = shoeMaterials;
            driver.WheelDiscOverlayMaterials = wheelDiscMaterials;
            driver.WheelTreadOverlayMaterials = wheelTreadMaterials;
            driverGo.transform.SetParent(__instance.transform, worldPositionStays: false);
            driverGo.SetActive(true);
        }

        private static Material[] TryCreateOverlay(Renderer parentRenderer)
        {
            // Idempotency: skip if we already attached an overlay child here.
            if (parentRenderer.transform.Find(OverlayChildName) != null) return null;

            var meshFilter = parentRenderer.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null) return null;

            var go = new GameObject(OverlayChildName);
            go.transform.SetParent(parentRenderer.transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = meshFilter.sharedMesh;

            var mr = go.AddComponent<MeshRenderer>();

            // One overlay material instance per submesh slot, so we can
            // drive each submesh's emission independently (e.g., wheel
            // disc submesh face color, wheel tread submesh rim color).
            int slotCount = Mathf.Max(1, parentRenderer.sharedMaterials.Length);
            var newMats = new Material[slotCount];
            for (int i = 0; i < slotCount; i++)
                newMats[i] = new Material(ExperimentEntry.EmissionOverlayShader);
            mr.sharedMaterials = newMats;

            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            return newMats;
        }

        private static float NearestWheelDistance(Vector3 worldPos, List<Transform> wheels)
        {
            float nearest = float.MaxValue;
            foreach (var w in wheels)
            {
                if (w == null) continue;
                float d = Vector3.Distance(worldPos, w.position);
                if (d < nearest) nearest = d;
            }
            return nearest;
        }

        private static string GetPath(Transform t)
        {
            if (t.parent == null) return t.name;
            return GetPath(t.parent) + "/" + t.name;
        }

        private static void LogDiscovery(Wheelset wheelset, List<Transform> wheels,
            Renderer[] all, List<Renderer> shoes, List<Renderer> wheelRenderers,
            int shoeOverlays, int wheelDiscOverlays, int wheelTreadOverlays)
        {
            var log = ExperimentEntry.Mod?.Logger;
            if (log == null) return;

            var sb = new StringBuilder();
            sb.AppendLine($"=== Brake emission discovery on Wheelset '{wheelset.name}' ===");
            sb.AppendLine($"Wheel transforms ({wheels.Count}):");
            for (int i = 0; i < wheels.Count; i++)
            {
                var w = wheels[i];
                sb.AppendLine(w == null ? $"  [{i}] <null>" : $"  [{i}] {w.name} @ {w.position}");
            }
            sb.AppendLine($"Total renderers under wheelset: {all.Length}");

            // Dump every unique material name so we can confirm whether
            // 'WheelTread' is even present under this hierarchy.
            var uniqueMats = new HashSet<string>();
            foreach (var r in all)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null) uniqueMats.Add(m.name);
            }
            sb.AppendLine($"Unique materials under wheelset ({uniqueMats.Count}): {string.Join(", ", uniqueMats)}");

            sb.AppendLine($"Shoe candidates ({shoes.Count}, radius={ShoeMatchRadius}m, path contains 'brakerig'):");
            foreach (var r in shoes)
            {
                float d = NearestWheelDistance(r.bounds.center, wheels);
                sb.AppendLine($"  - {GetPath(r.transform)}  (d={d:F2}m, mat={r.sharedMaterial?.name ?? "null"})");
            }
            sb.AppendLine($"Wheel renderers ({wheelRenderers.Count}, multi-submesh: disc + tread):");
            foreach (var r in wheelRenderers)
            {
                var matNames = r.sharedMaterials;
                var slotInfo = string.Join(", ", System.Linq.Enumerable.Select(matNames, m => m?.name ?? "null"));
                sb.AppendLine($"  - {GetPath(r.transform)}  (slots: {slotInfo})");
            }
            sb.AppendLine($"Overlays created: shoe={shoeOverlays}, wheelDisc materials={wheelDiscOverlays}, wheelTread materials={wheelTreadOverlays}");
            sb.AppendLine($"=== End discovery ===");
            log.Log(sb.ToString());
        }
    }
}
