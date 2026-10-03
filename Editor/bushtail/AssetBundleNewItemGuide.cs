#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BundleDumperInternal.AssetsTools.NET;
using BundleDumperInternal.AssetsTools.NET.Extra;
using UnityEditor;
using UnityEngine;

namespace Editor.bushtail
{
    /// <summary>Guides the manual parts of the WeaponAIOTool workflow after automated extraction.</summary>
    internal sealed class AssetBundleNewItemGuide : EditorWindow
    {
        private static readonly string[] Steps =
        {
            "Edit model and animations", "Inspect Unity assets", "Register the bundle", "Create item data", "Test in game"
        };
        private const string Prefix = "bushtail.NewItemGuide.";
        private const string PendingKey = Prefix + "pendingRoots";
        [SerializeField] private List<string> roots = new();
        [SerializeField] private int selected;
        [SerializeField] private int step;
        private Vector2 scroll;
        private string status = "";
        private bool isWeapon;
        private string prefabPath = "";
        private string bundlePath = "";
        private string bundleKey = "";
        private string[] cabs = Array.Empty<string>();
        private string contextRoot = "";

        [MenuItem("Assets/bushtail/New Item Guide", false, 2005)]
        private static void OpenSelected()
        {
            var root = AssetBundleDumpBuilder.FindDumpRoot(AssetDatabase.GetAssetPath(Selection.activeObject));
            if (root == null) { throw new InvalidOperationException("Select an asset inside a completed bundle dump."); }
            Open(new[] { root });
        }

        [MenuItem("Assets/bushtail/New Item Guide", true)]
        private static bool CanOpenSelected() => AssetBundleDumpBuilder.FindDumpRoot(
            AssetDatabase.GetAssetPath(Selection.activeObject)) != null;

        internal static void Open(IEnumerable<string> dumpRoots)
        {
            if (Application.isBatchMode) { return; }
            var window = CreateInstance<AssetBundleNewItemGuide>();
            window.titleContent = new GUIContent("New Item Guide");
            window.minSize = new Vector2(560, 425);
            window.roots = dumpRoots.Where(AssetDatabase.IsValidFolder).Distinct(StringComparer.Ordinal).ToList();
            window.ShowUtility();
            window.Focus();
        }

        internal static void QueueOpen(IEnumerable<string> dumpRoots)
        {
            if (Application.isBatchMode) { return; }
            SessionState.SetString(PendingKey, string.Join("\n", dumpRoots));
            EditorApplication.delayCall += OpenPending;
        }

        [InitializeOnLoadMethod]
        private static void OpenAfterReload()
        {
            EditorApplication.delayCall += OpenPending;
        }

        private static void OpenPending()
        {
            if (Application.isBatchMode) { return; }
            var pending = SessionState.GetString(PendingKey, "");
            if (string.IsNullOrEmpty(pending)) { return; }
            SessionState.EraseString(PendingKey);
            Open(pending.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static void SaveDisplayName(string root, string name)
        {
            var guid = AssetDatabase.AssetPathToGUID(root);
            if (!string.IsNullOrEmpty(guid)) { EditorPrefs.SetString(Prefix + guid + ".name", name); }
        }

        private void OnGUI()
        {
            roots.RemoveAll(root => !AssetDatabase.IsValidFolder(root));
            if (roots.Count == 0)
            {
                EditorGUILayout.HelpBox("No completed dump is available. Select a dump folder and reopen Assets > bushtail > New Item Guide.", MessageType.Info);
                return;
            }
            selected = Mathf.Clamp(selected, 0, roots.Count - 1);
            if (roots.Count > 1)
            {
                var choice = EditorGUILayout.Popup("Item", selected, roots.Select(Path.GetFileName).ToArray());
                if (choice != selected) { selected = choice; step = 0; contextRoot = ""; }
            }
            var root = roots[selected];
            if (contextRoot != root) { LoadContext(root); }
            var guid = AssetDatabase.AssetPathToGUID(root);
            var displayName = EditorPrefs.GetString(Prefix + guid + ".name", Path.GetFileName(root));
            EditorGUILayout.LabelField(displayName, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Bundle key", bundleKey);
            var completed = Enumerable.Range(0, Steps.Length).Count(index => EditorPrefs.GetBool(DoneKey(guid, index)));
            EditorGUILayout.LabelField("Manual tasks", completed + " of " + Steps.Length + " marked done");
            step = EditorGUILayout.Popup("Current task", Mathf.Clamp(step, 0, Steps.Length - 1), Steps);
            EditorGUILayout.Space(5);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            switch (step)
            {
                case 0: DrawEditing(root); break;
                case 1: DrawInspection(root); break;
                case 2: DrawBundle(root); break;
                case 3: DrawItemData(displayName); break;
                case 4: DrawGameTest(); break;
            }
            EditorGUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(status)) { EditorGUILayout.HelpBox(status, MessageType.Info); }

            var done = EditorPrefs.GetBool(DoneKey(guid, step));
            var updated = EditorGUILayout.ToggleLeft("I completed this task", done);
            if (updated != done) { EditorPrefs.SetBool(DoneKey(guid, step), updated); }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(step == 0))
                {
                    if (GUILayout.Button("Previous")) { step--; status = ""; }
                }
                using (new EditorGUI.DisabledScope(step == Steps.Length - 1))
                {
                    if (GUILayout.Button("Next"))
                    {
                        if (updated || EditorUtility.DisplayDialog("Task still pending",
                                "This task is not marked done. Continue and leave it pending?", "Continue", "Stay here"))
                        {
                            step++;
                            status = "";
                        }
                    }
                }
                if (GUILayout.Button("Close")) { Close(); }
            }
        }

        private void LoadContext(string root)
        {
            contextRoot = root;
            status = "";
            cabs = Array.Empty<string>();
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { root }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
            prefabPath = prefabs.FirstOrDefault(path =>
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                return prefab && HasWeaponReference(prefab);
            }) ?? prefabs.FirstOrDefault() ?? "";
            isWeapon = !string.IsNullOrEmpty(prefabPath) && HasWeaponReference(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            try
            {
                bundleKey = AssetBundleDumpBuilder.ReadSettings(root).BundleName.ToLowerInvariant();
                bundlePath = Path.Combine(project, "AssetBundles", "Dumps", Path.GetFileName(root),
                    bundleKey.Replace('/', Path.DirectorySeparatorChar));
            }
            catch (Exception exception)
            {
                bundleKey = "Build metadata unavailable";
                bundlePath = "";
                status = exception.Message;
            }
        }

        private void DrawEditing(string root)
        {
            EditorGUILayout.HelpBox(isWeapon
                ? "Edit the dumped model, mesh, materials, and animation assets in Unity. Keep the weapon hierarchy, bone names, clip names, and controller references compatible with the original container. Inspect the pivot, attachment points, and firing and reload poses after edits."
                : "Edit the dumped model or prefab in Unity. Check its pivot, materials, collider, scale, and attachment points. The source bundle and references were already extracted and remapped.", MessageType.Info);
            SelectPrefabButton();
            if (GUILayout.Button("Rebuild after Unity edits")) { Build(root); }
        }

        private void DrawInspection(string root)
        {
            if (isWeapon)
            {
                EditorGUILayout.HelpBox("Inspect the weapon container's Weapon Object and original Animator Controller. Check TransformLinks, muzzle/attachment points, left-hand actions, Avatar Masks and IK, animation events, SoundBanks, and the Standart BlendOptions reference. Compare firing, reload, and empty-magazine poses in Unity.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Inspect the item's prefab, materials, pivot, physics/collider setup, and any missing script or object references.", MessageType.Info);
            }
            EditorGUILayout.LabelField("Prefab", string.IsNullOrEmpty(prefabPath) ? "No prefab found" : prefabPath, EditorStyles.wordWrappedLabel);
            SelectPrefabButton();
            if (GUILayout.Button("Rebuild after Unity edits")) { Build(root); }
        }

        private void DrawBundle(string root)
        {
            EditorGUILayout.HelpBox("Copy the verified bundle into your mod's bundle directory. Add its exact key to bundles.json, and map its built CAB in cab_manifest.json if your loader uses that file. Include the original shared dependency keys actually referenced by the bundle; check cubemaps, shaders, and physicsmaterials first.", MessageType.Info);
            EditorGUILayout.LabelField("Built bundle", string.IsNullOrEmpty(bundlePath) ? "Unknown" : bundlePath, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("Bundle key", bundleKey);
            if (!string.IsNullOrEmpty(bundlePath) && File.Exists(bundlePath))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reveal built bundle")) { EditorUtility.RevealInFinder(bundlePath); }
                    if (GUILayout.Button("Read CAB IDs"))
                    {
                        try { cabs = ReadCabs(bundlePath); status = "Read " + cabs.Length + " serialized CAB ID(s) from the built bundle."; }
                        catch (Exception exception) { Report(exception); }
                    }
                }
                foreach (var cab in cabs) { EditorGUILayout.SelectableLabel(cab, GUILayout.Height(18)); }
                if (GUILayout.Button("Copy bundle key and CAB IDs"))
                {
                    if (cabs.Length == 0)
                    {
                        try { cabs = ReadCabs(bundlePath); }
                        catch (Exception exception) { Report(exception); return; }
                    }
                    EditorGUIUtility.systemCopyBuffer = "Bundle key: " + bundleKey + "\n" +
                        string.Join("\n", cabs.Select(cab => "CAB: " + cab));
                    status = "Bundle details copied. Verify dependency keys before editing your mod manifests.";
                }
            }
            else if (GUILayout.Button("Build and verify now")) { Build(root); }

            var modFolder = EditorPrefs.GetString(Prefix + "modFolder", "");
            EditorGUILayout.LabelField("Mod project", string.IsNullOrEmpty(modFolder) ? "Choose a destination" : modFolder, EditorStyles.wordWrappedLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Choose mod folder"))
                {
                    var chosen = EditorUtility.OpenFolderPanel("Mod project folder", modFolder, "");
                    if (!string.IsNullOrEmpty(chosen)) { EditorPrefs.SetString(Prefix + "modFolder", chosen); }
                }
                using (new EditorGUI.DisabledScope(!Directory.Exists(modFolder)))
                {
                    if (GUILayout.Button("Reveal mod folder")) { EditorUtility.RevealInFinder(modFolder); }
                }
            }
        }

        private void DrawItemData(string displayName)
        {
            EditorGUILayout.HelpBox("Create a unique item template ID in your server mod and clone a compatible base item. Set its Prefab.path to the bundle key shown above. Supply the display name, short name, description, handbook category, price, and any trader or bot entries. For weapons, review caliber, slots/filters, fire modes, firing rate, presets, and mastery. These values depend on your mod and cannot be inferred safely from the bundle.", MessageType.Info);
            EditorGUILayout.LabelField("Suggested display name", displayName);
            EditorGUILayout.LabelField("Prefab.path", bundleKey);
            if (GUILayout.Button("Copy bundle key for item data"))
            {
                EditorGUIUtility.systemCopyBuffer = bundleKey;
                status = "Bundle key copied.";
            }
        }

        private void DrawGameTest()
        {
            if (isWeapon)
            {
                EditorGUILayout.HelpBox("Install the mod and test equip, aiming, fire, reload, empty magazine, malfunction, attachments, muzzle effects, and suppressed/unsuppressed audio. Check slide and recoil timing and both server and client logs. Rebuild after any Unity edit.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Install the mod and test spawning, inventory/world appearance, interactions, icons, physics, and server/client logs. Rebuild after any Unity edit.", MessageType.Info);
            }
            if (!string.IsNullOrEmpty(bundlePath) && File.Exists(bundlePath) && GUILayout.Button("Reveal bundle for final install"))
            {
                EditorUtility.RevealInFinder(bundlePath);
            }
        }

        private void SelectPrefabButton()
        {
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(prefabPath)))
            {
                if (GUILayout.Button("Select original prefab in Unity")) { SelectAsset(prefabPath); }
            }
        }

        private static bool HasWeaponReference(GameObject prefab)
        {
            if (!prefab) { return false; }
            foreach (var component in prefab.GetComponents<MonoBehaviour>())
            {
                if (!component) { continue; }
                var reference = new SerializedObject(component).FindProperty("_weaponObject");
                if (reference?.objectReferenceValue is GameObject) { return true; }
            }
            return false;
        }

        private void Build(string root)
        {
            try
            {
                bundlePath = AssetBundleDumpBuilder.Build(root);
                cabs = Array.Empty<string>();
                status = "Build and serialized-reference verification passed: " + bundlePath;
            }
            catch (Exception exception) { Report(exception); }
        }

        private void Report(Exception exception)
        {
            status = exception.Message;
            Debug.LogException(exception);
        }

        private static void SelectAsset(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (!asset) { return; }
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private static string[] ReadCabs(string path)
        {
            var manager = new AssetsManager();
            try
            {
                var bundle = manager.LoadBundleFile(path);
                var cabs = new List<string>();
                for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                {
                    if (AssetBundleDependencies.IsSerializedFile(bundle.file, i))
                    {
                        cabs.Add(bundle.file.GetFileName(i).ToLowerInvariant());
                    }
                }
                return cabs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(cab => cab, StringComparer.Ordinal).ToArray();
            }
            finally { manager.UnloadAll(true); }
        }

        private static string DoneKey(string guid, int index) => Prefix + guid + ".step" + index;
    }
}
