using UnityEngine;

namespace Ca.Jwsm.Railroader.Ui.Shared
{
    /// <summary>
    /// Marks a child of a UI prefab as a named slot that runtime code can find.
    /// The renderer uses GetComponentsInChildren&lt;UIAnchor&gt;() to locate slots
    /// by Id, decoupling C# wiring from the prefab's exact GameObject hierarchy.
    /// Designers can rearrange the prefab freely as long as anchor Ids stay the same.
    /// </summary>
    /// <remarks>
    /// This script must exist in BOTH the mod runtime (here) AND the authoring
    /// Unity project, with the SAME namespace and type name. Prefabs serialize
    /// component references by full type name; if the namespace differs, the
    /// runtime's loaded bundle will fail to resolve UIAnchor references.
    ///
    /// Conventions for Id:
    ///   - lower-kebab-case
    ///   - unique within a single prefab
    ///   - examples: "title", "content-area", "close-button", "footer"
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class UIAnchor : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("The identifier renderer code uses to find this slot. Lower-kebab-case, unique within the prefab.")]
        private string id;

        public string Id => id;
    }
}
