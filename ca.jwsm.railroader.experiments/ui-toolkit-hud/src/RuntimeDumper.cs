using System;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityModManagerNet;

namespace Ca.Jwsm.Railroader.Experiments.UiToolkitHud
{
    /// <summary>
    /// Runtime UI structure dumper. Press F8 in-game to capture the live
    /// scene's UI hierarchy to a timestamped text file in the mod's dumps/
    /// folder. Required for inspecting dynamically-instantiated UI like
    /// the CarInspector — these GameObjects only exist while the relevant
    /// window is open, so runtime inspection is the only reliable path.
    ///
    /// Output format mirrors the editor-side UIStructureDumper but with
    /// NO TMP text truncation — full text content is captured so rich-text
    /// formatting (e.g. multi-line speed readouts with size/color tags) can
    /// be read accurately.
    ///
    /// Lives in the experiments mod since it's a research tool, not
    /// production code. The patterns it captures inform clones in the
    /// experiment; the dumper itself never graduates.
    /// </summary>
    public static class RuntimeDumper
    {
        /// <summary>
        /// Finds every GameObject hosting a component of type T (including in
        /// inactive objects) and dumps each one's hierarchy. The right way to
        /// locate UI like CarInspector that's identified by [RequireComponent]
        /// rather than by GameObject name.
        /// </summary>
        public static void DumpByComponent<T>(UnityModManager.ModEntry modEntry, string label) where T : Component
        {
            var sb = new StringBuilder();
            // FindObjectsOfTypeAll catches inactive objects (active scene
            // CarInspector might be enabled-but-window-hidden, or fully inactive).
            var matches = Resources.FindObjectsOfTypeAll<T>();
            sb.AppendLine($"# Found {matches.Length} GameObject(s) hosting {typeof(T).FullName}");
            sb.AppendLine();
            foreach (var c in matches)
            {
                if (c == null || c.gameObject == null) continue;
                sb.AppendLine($"--- match: '{c.gameObject.name}' (full path: {GetPath(c.transform)}) scene='{c.gameObject.scene.name}' ---");
                DumpGameObject(c.gameObject, 0, sb);
                sb.AppendLine();
            }
            WriteToFile(modEntry, $"runtime-component-{label}", sb.ToString());
        }

        public static void DumpAll(UnityModManager.ModEntry modEntry)
        {
            var sb = new StringBuilder();
            ForEachRoot(root =>
            {
                DumpGameObject(root, 0, sb);
            }, sb);
            WriteToFile(modEntry, "runtime-all", sb.ToString());
        }

        /// <summary>
        /// Dumps every GameObject whose name matches (case-insensitive contains
        /// or exact). Walks all loaded scenes plus DontDestroyOnLoad. Useful
        /// when the target's name isn't an exact match (e.g. "CarInspector"
        /// might actually be "Car Inspector" or "CarInspectorPanel").
        /// </summary>
        public static void DumpByName(UnityModManager.ModEntry modEntry, string nameFragment)
        {
            var sb = new StringBuilder();
            var found = 0;
            ForEachRoot(root =>
            {
                CollectMatching(root.transform, nameFragment, sb, ref found);
            }, null);
            if (found == 0)
            {
                sb.AppendLine($"(no GameObject containing '{nameFragment}' found across all loaded scenes + DontDestroyOnLoad)");
            }
            else
            {
                sb.Insert(0, $"# Found {found} match(es) for '{nameFragment}'\n\n");
            }
            WriteToFile(modEntry, $"runtime-{Sanitize(nameFragment)}", sb.ToString());
        }

        /// <summary>
        /// Visits every root GameObject in every loaded scene plus the
        /// special DontDestroyOnLoad scene. Pass an optional headerSink to
        /// receive scene-divider headers.
        /// </summary>
        private static void ForEachRoot(Action<GameObject> visit, StringBuilder headerSink)
        {
            // All standard loaded scenes
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                headerSink?.AppendLine($"=== Scene: {scene.name} ===");
                foreach (var root in scene.GetRootGameObjects())
                    visit(root);
            }

            // DontDestroyOnLoad scene — accessed via a temporary marker
            // (Unity doesn't expose it directly through SceneManager).
            var temp = new GameObject("__ddol_dump_marker__");
            UnityEngine.Object.DontDestroyOnLoad(temp);
            var ddolScene = temp.scene;
            try
            {
                headerSink?.AppendLine($"=== Scene: DontDestroyOnLoad ===");
                foreach (var root in ddolScene.GetRootGameObjects())
                {
                    if (root != temp) visit(root);
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(temp);
            }
        }

        private static void CollectMatching(Transform t, string nameFragment, StringBuilder sb, ref int count)
        {
            if (t.name.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sb.AppendLine($"--- match: '{t.name}' (full path: {GetPath(t)}) ---");
                DumpGameObject(t.gameObject, 0, sb);
                sb.AppendLine();
                count++;
                return;  // don't recurse into matched root — its descendants are already dumped
            }
            foreach (Transform child in t)
                CollectMatching(child, nameFragment, sb, ref count);
        }

        private static string GetPath(Transform t)
        {
            if (t.parent == null) return "/" + t.name;
            return GetPath(t.parent) + "/" + t.name;
        }

        private static void WriteToFile(UnityModManager.ModEntry modEntry, string label, string content)
        {
            try
            {
                var dumpDir = Path.Combine(modEntry.Path, "dumps");
                Directory.CreateDirectory(dumpDir);
                var fileName = $"{label}-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt";
                var fullPath = Path.Combine(dumpDir, fileName);
                File.WriteAllText(fullPath, content);
                modEntry.Logger.Log($"Runtime dump written: {fullPath}");
            }
            catch (Exception ex)
            {
                modEntry.Logger.Error($"Failed to write dump: {ex}");
            }
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        // ---- The walker ----

        private static void DumpGameObject(GameObject go, int depth, StringBuilder sb)
        {
            var indent = new string(' ', depth * 2);
            sb.Append(indent).Append("- ").Append(go.name);
            if (!go.activeSelf) sb.Append(" [INACTIVE]");
            sb.AppendLine();

            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                sb.Append(indent).Append("    rt: anchorMin=").Append(F2(rt.anchorMin))
                  .Append(" anchorMax=").Append(F2(rt.anchorMax))
                  .Append(" pivot=").Append(F2(rt.pivot))
                  .Append(" anchoredPos=").Append(F2(rt.anchoredPosition))
                  .Append(" sizeDelta=").Append(F2(rt.sizeDelta))
                  .AppendLine();
            }

            var canvas = go.GetComponent<Canvas>();
            if (canvas != null)
                sb.Append(indent).Append("    canvas: renderMode=").Append(canvas.renderMode)
                  .Append(" sortOrder=").Append(canvas.sortingOrder).AppendLine();

            var scaler = go.GetComponent<CanvasScaler>();
            if (scaler != null)
                sb.Append(indent).Append("    scaler: mode=").Append(scaler.uiScaleMode)
                  .Append(" refRes=").Append(scaler.referenceResolution)
                  .Append(" match=").Append(scaler.matchWidthOrHeight)
                  .Append(" scaleFactor=").Append(scaler.scaleFactor).AppendLine();

            var img = go.GetComponent<Image>();
            if (img != null)
                sb.Append(indent).Append("    image: sprite=").Append(img.sprite != null ? img.sprite.name : "(none)")
                  .Append(" type=").Append(img.type).Append(" color=").Append(ColorToHex(img.color)).AppendLine();

            var rawImg = go.GetComponent<RawImage>();
            if (rawImg != null)
                sb.Append(indent).Append("    rawImage: texture=").Append(rawImg.texture != null ? rawImg.texture.name : "(none)")
                  .Append(" color=").Append(ColorToHex(rawImg.color)).AppendLine();

            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                // No truncation — capture full text including TMP rich-text tags
                var text = tmp.text ?? string.Empty;
                sb.Append(indent).Append("    tmp: text=\"").Append(text).Append("\"")
                  .Append(" font=").Append(tmp.font != null ? tmp.font.name : "(none)")
                  .Append(" size=").Append(tmp.fontSize).Append(" color=").Append(ColorToHex(tmp.color))
                  .Append(" align=").Append(tmp.alignment).AppendLine();
            }

            var hlg = go.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
                sb.Append(indent).Append("    horizLayout: padding=").Append(hlg.padding)
                  .Append(" spacing=").Append(hlg.spacing).Append(" childAlign=").Append(hlg.childAlignment)
                  .Append(" controlW/H=").Append(hlg.childControlWidth).Append("/").Append(hlg.childControlHeight).AppendLine();

            var vlg = go.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
                sb.Append(indent).Append("    vertLayout: padding=").Append(vlg.padding)
                  .Append(" spacing=").Append(vlg.spacing).Append(" childAlign=").Append(vlg.childAlignment)
                  .Append(" controlW/H=").Append(vlg.childControlWidth).Append("/").Append(vlg.childControlHeight).AppendLine();

            var grid = go.GetComponent<GridLayoutGroup>();
            if (grid != null)
                sb.Append(indent).Append("    gridLayout: cellSize=").Append(grid.cellSize)
                  .Append(" spacing=").Append(grid.spacing).Append(" padding=").Append(grid.padding)
                  .Append(" startCorner=").Append(grid.startCorner).AppendLine();

            var fitter = go.GetComponent<ContentSizeFitter>();
            if (fitter != null)
                sb.Append(indent).Append("    sizeFitter: H=").Append(fitter.horizontalFit)
                  .Append(" V=").Append(fitter.verticalFit).AppendLine();

            if (go.GetComponent<Mask>() != null) sb.Append(indent).Append("    mask").AppendLine();
            if (go.GetComponent<RectMask2D>() != null) sb.Append(indent).Append("    rectMask2D").AppendLine();

            var scroll = go.GetComponent<ScrollRect>();
            if (scroll != null)
                sb.Append(indent).Append("    scrollRect: H=").Append(scroll.horizontal)
                  .Append(" V=").Append(scroll.vertical).Append(" movement=").Append(scroll.movementType).AppendLine();

            foreach (Transform child in go.transform)
                DumpGameObject(child.gameObject, depth + 1, sb);
        }

        private static string F2(Vector2 v) => $"({v.x:0.##},{v.y:0.##})";

        private static string ColorToHex(Color c)
        {
            var r = (int)(c.r * 255); var g = (int)(c.g * 255); var b = (int)(c.b * 255); var a = (int)(c.a * 255);
            return a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{r:X2}{g:X2}{b:X2}{a:X2}";
        }
    }
}
