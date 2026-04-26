# UI Authoring Workflow

How visual UI work flows from your separate Unity authoring project into the `ca.jwsm.railroader.ui` mod.

## The two-project setup

UI work happens in **two places**:

```
Auxiliary authoring project                    Mod monorepo
(separate Unity project, NOT in our repo)      (this repo)
─────────────────────────────────────          ─────────────────────────────
Visual prefab design (Unity editor)            Runtime C# (loaders, renderers)
AssetBundle build pipeline                     Mod assembly built by dotnet
Outputs: .bundle files                         Inputs: .bundle files
```

The authoring project is a regular Unity project. The mod monorepo is a regular .NET solution. The `.bundle` files are the bridge: built in Unity, loaded by the mod at runtime.

## One-time setup

### 1. Install Unity 2022.3.62f2 (LTS)

Match the game's Unity version exactly. Different versions break asset compatibility.

- Install Unity Hub: https://unity.com/download
- In Hub: **Installs → Install Editor → Archive tab → search `2022.3.62f2`**
- Default modules are fine; ensure *Windows Build Support (Mono)* is included

### 2. Create the authoring Unity project

- Template: **Universal 3D (URP)**. The game uses URP (`Unity.RenderPipelines.Universal.*` is in its Managed folder), so materials authored in Built-In RP would render incorrectly when loaded into the game.
- Project name: anything descriptive (`RailroaderUI`, `JwsmAuthoring`). Keep it **outside** `Game_Projects/Railroader/` — it's an auxiliary project, not part of the mod monorepo.
- Once open, verify TextMeshPro is included (it ships with the URP template). If prompted on first scene to "import TMP Essential Resources," accept.

### 3. Add the shared `UIAnchor` script

This MonoBehaviour exists in two places — once in the mod runtime (already created at [ca.jwsm.railroader.ui/Shared/UIAnchor.cs](../ca.jwsm.railroader.ui/Shared/UIAnchor.cs)), and once in your authoring project. **The namespace and type name MUST match exactly.** Prefabs serialize component references by full type name; if the namespace differs, the runtime won't resolve them.

In the authoring Unity project, create folder `Assets/Scripts/Shared/`. Inside, create `UIAnchor.cs` with this content (mirror of the mod-runtime version):

```csharp
using UnityEngine;

namespace Ca.Jwsm.Railroader.Ui.Shared
{
    [DisallowMultipleComponent]
    public sealed class UIAnchor : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("The identifier renderer code uses to find this slot. Lower-kebab-case, unique within the prefab.")]
        private string id;

        public string Id => id;
    }
}
```

When this and the mod-runtime version drift apart, prefabs break at runtime. **Keep them in sync.** Future option: promote both into a shared assembly. For now, manual sync.

### 4. Add the AssetBundle build script

Create folder `Assets/Editor/` in the authoring project. Inside, create `BuildUIBundles.cs`:

```csharp
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildUIBundles
{
    private const string DestKey = "RailroaderUI.BundleDestPath";

    [MenuItem("Railroader UI/Build AssetBundles")]
    public static void Build()
    {
        var dest = EditorPrefs.GetString(DestKey, string.Empty);
        if (string.IsNullOrEmpty(dest) || !Directory.Exists(dest))
        {
            dest = EditorUtility.OpenFolderPanel(
                "Choose destination for built AssetBundles",
                Application.dataPath, string.Empty);
            if (string.IsNullOrEmpty(dest))
            {
                Debug.LogWarning("Bundle build cancelled (no destination chosen).");
                return;
            }
            EditorPrefs.SetString(DestKey, dest);
        }

        Directory.CreateDirectory(dest);
        var manifest = BuildPipeline.BuildAssetBundles(
            dest,
            BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);

        if (manifest == null)
        {
            Debug.LogError("AssetBundle build failed. Check console for errors.");
        }
        else
        {
            Debug.Log($"AssetBundles built to {dest}");
            EditorUtility.RevealInFinder(dest);
        }
    }

    [MenuItem("Railroader UI/Reset Bundle Destination")]
    public static void ResetDest()
    {
        EditorPrefs.DeleteKey(DestKey);
        Debug.Log("Bundle destination reset. Next build will prompt for a new path.");
    }
}
```

What it does:
- Adds menu items under `Railroader UI` in Unity's top menu bar
- First build prompts for an output folder; remembers it for next time
- Builds all marked AssetBundles to that folder
- "Reset Bundle Destination" clears the remembered path

Recommended destination: `Railroader/ca.jwsm.railroader.ui/Resources/bundles/` (inside this monorepo). The mod's runtime loader will pick them up from there.

### 5. Configure the GameDir env var (optional)

The monorepo's `Directory.Build.props` defaults to `D:\SteamLibrary\steamapps\common\Railroader`. If your game lives elsewhere:

```powershell
[Environment]::SetEnvironmentVariable("GAME_DIR", "X:\path\to\Railroader", "User")
```

Restart your shell after setting. Or override per-session: `$env:GAME_DIR = "..."`.

## The authoring loop

Once setup is done, the day-to-day rhythm:

1. **Design a prefab** in the Unity editor. Add `UIAnchor` components to children that runtime code needs to find (title text, content area, close button, etc.). Set their `Id` field to a stable string.
2. **Mark the prefab for an AssetBundle** in its inspector (bottom of the asset properties). Use a consistent name like `ui-shared` or `ui-chrome`.
3. **Build:** `Menu → Railroader UI → Build AssetBundles`. Bundles output to your chosen destination.
4. **Mod runtime picks them up** — the C# loader in `ca.jwsm.railroader.ui` reads them from `Resources/bundles/`, looks up prefabs by name, instantiates as needed.

## Conventions

- **Anchor IDs** — lower-kebab-case (`title`, `content-area`, `close-button`). Stable across prefab variants.
- **Bundle names** — lower-kebab-case (`ui-shared`, `ui-chrome`, `ui-icons`). Start with one bundle (`ui-shared`); split when there's a real reason.
- **Prefab names** — `WindowChrome`, `ModalChrome`, `PrimaryButton`, etc. PascalCase, descriptive.
- **Theme tokens** — never hardcode colors in prefab inspectors. Use a `ThemeApplicator` component (TBD; coming as the theme system lands) so colors come from the theme service at runtime.

## Anti-patterns

- **Don't reference scripts from the mod runtime by full path** in your authoring project — that creates a circular dependency. The shared scripts (`UIAnchor` etc.) are intentionally tiny and duplicated.
- **Don't author prefabs that hardcode Unity-version-specific features** (URP-specific shader properties, new-Input-System bindings beyond standard UI events). Stick to portable UI components.
- **Don't put scenes in your authoring project that try to "preview" UI in-game context** — too brittle. Author prefabs in isolation; preview in-game by loading the mod.

## When you're stuck

- Bundle builds but nothing visible in-game → anchor namespace/name mismatch is the most common cause. Double-check `UIAnchor.cs` matches in both projects.
- Prefab references missing on load → bundle was built against a different Unity version, or a script was renamed/moved.
- "Pink" materials in-game → URP/Built-In RP mismatch. Confirm the authoring project is URP.
