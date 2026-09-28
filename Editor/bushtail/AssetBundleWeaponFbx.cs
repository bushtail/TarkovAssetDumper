#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
using UnityEngine;

namespace Editor.bushtail
{
    /// <summary>Creates an editing copy of a ripped weapon, outside the bundle source tree.</summary>
    internal static class AssetBundleWeaponFbx
    {
        internal const string OutputRoot = "Assets/BundleDumperFBX";

        [MenuItem("Assets/bushtail/Export Editable Weapon FBX", false, 2003)]
        private static void ExportSelected()
        {
            var log = new List<string>();
            ExportFromDump(AssetDatabase.GetAssetPath(Selection.activeObject), log);
            Debug.Log(string.Join("\n", log));
        }

        [MenuItem("Assets/bushtail/Export Editable Weapon FBX", true)]
        private static bool CanExportSelected()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return AssetDatabase.IsValidFolder(path)
                && AssetDatabase.GetLabels(Selection.activeObject).Contains("BundleDumperExport");
        }

        internal static void ExportFromDump(string dumpRoot, List<string> log, string? assetStudioOutput = null)
        {
            var count = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { dumpRoot }))
            {
                var containerPath = AssetDatabase.GUIDToAssetPath(guid);
                var container = AssetDatabase.LoadAssetAtPath<GameObject>(containerPath);
                if (!container || !TryGetWeaponReferences(container, out var model, out var controller))
                {
                    continue;
                }

                var modelPath = AssetDatabase.GetAssetPath(model);
                if (string.IsNullOrEmpty(modelPath) || !modelPath.StartsWith(dumpRoot + "/", StringComparison.Ordinal))
                {
                    log.Add("FBX SKIPPED: " + containerPath + " has no imported weapon model in this dump.");
                    continue;
                }

                try
                {
                    if (assetStudioOutput == null || !ImportAssetStudioModel(dumpRoot, model, assetStudioOutput, log))
                    {
                        ExportModel(dumpRoot, modelPath, controller, log);
                    }
                    count++;
                }
                catch (Exception exception)
                {
                    log.Add("FBX FAILED: " + containerPath + ": " + exception);
                    Debug.LogException(exception);
                }
            }

            if (count == 0)
            {
                log.Add("FBX: No editable weapon model was exported from " + dumpRoot + ".");
            }
        }

        private static bool ImportAssetStudioModel(string dumpRoot, GameObject model, string output, List<string> log)
        {
            var source = Path.Combine(output, "Animator");
            if (!Directory.Exists(source)) { return false; }
            var matches = Directory.GetFiles(source, "*.fbx", SearchOption.AllDirectories)
                .Where(path => Path.GetFileNameWithoutExtension(path).Equals(model.name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                log.Add("FBX WARNING: Expected one AssetStudio model named " + model.name + "; found " + matches.Length + ". Using Unity export.");
                return false;
            }

            var dumpName = dumpRoot.Substring(dumpRoot.LastIndexOf('/') + 1);
            EnsureFolder(OutputRoot);
            var folder = OutputRoot + "/" + dumpName;
            EnsureFolder(folder);
            var assetPath = folder + "/" + model.name + ".fbx";
            var projectRoot = Path.GetDirectoryName(Application.dataPath)!;
            var destination = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            File.Copy(matches[0], destination, true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var imported = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            var clips = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<AnimationClip>()
                .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
                .ToArray();
            var animated = clips.Count(clip => AnimationUtility.GetCurveBindings(clip).Length > 0);
            if (!imported || animated == 0 || !imported.GetComponentsInChildren<Renderer>(true).Any())
            {
                AssetDatabase.DeleteAsset(assetPath);
                log.Add("FBX WARNING: AssetStudio output has no usable mesh and animation curves; using Unity export. Check " + Path.Combine(output, "Animator"));
                return false;
            }

            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer)
            {
                importer.SetAssetBundleNameAndVariant("", "");
                TagSource(importer, assetPath, dumpRoot);
            }
            log.Add("FBX: AssetStudio " + model.name + " -> " + assetPath + " (" + animated + " editable clips; source build assets preserved)");
            return true;
        }

        internal static bool TryGetWeaponReferences(GameObject container, out GameObject model, out RuntimeAnimatorController? controller)
        {
            model = null!;
            controller = null;
            foreach (var component in container.GetComponents<MonoBehaviour>())
            {
                if (!component) { continue; }
                var serialized = new SerializedObject(component);
                var modelProperty = serialized.FindProperty("_weaponObject");
                if (modelProperty?.objectReferenceValue is not GameObject referencedModel) { continue; }

                model = referencedModel;
                controller = serialized.FindProperty("_originalAnimatorController")?.objectReferenceValue as RuntimeAnimatorController;
                return true;
            }

            return false;
        }

        private static void ExportModel(string dumpRoot, string modelPath, RuntimeAnimatorController? controller, List<string> log)
        {
            var instance = PrefabUtility.LoadPrefabContents(modelPath);
            AnimatorOverrideController? temporaryController = null;
            try
            {
                var animator = instance.GetComponentInChildren<Animator>(true);
                if (!animator)
                {
                    throw new InvalidOperationException("Weapon model has no Animator: " + modelPath);
                }

                var sourceController = controller ? controller : animator.runtimeAnimatorController;
                if (!sourceController)
                {
                    throw new InvalidOperationException("Container and weapon model have no usable Animator Controller: " + modelPath);
                }

                var clips = sourceController!.animationClips.Where(clip => clip).Distinct().ToArray();
                if (clips.Length == 0)
                {
                    throw new InvalidOperationException("Animator Controller contains no animation clips: " + modelPath);
                }

                // AssetRipper controllers can have no default state in their first
                // layer. Unity's FBX exporter dereferences it. An in-memory override
                // controller exposes the same clips without that editor-only lookup.
                temporaryController = new AnimatorOverrideController(sourceController);
                animator.runtimeAnimatorController = temporaryController;

                // AssetRipper can leave an offset on the model root. Export a temporary
                // copy at the origin; keep child/bone transforms and the source prefab intact.
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;

                var dumpName = dumpRoot.Substring(dumpRoot.LastIndexOf('/') + 1);
                EnsureFolder(OutputRoot);
                var outputFolder = OutputRoot + "/" + dumpName;
                EnsureFolder(outputFolder);
                var assetPath = outputFolder + "/" + Path.GetFileNameWithoutExtension(modelPath) + ".fbx";
                var projectRoot = Path.GetDirectoryName(Application.dataPath)!;
                var absolutePath = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
                var result = ModelExporter.ExportObject(absolutePath, instance);
                if (string.IsNullOrEmpty(result) || !File.Exists(absolutePath))
                {
                    throw new IOException("Unity FBX Exporter did not create " + assetPath);
                }

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(assetPath);
                if (importer)
                {
                    importer.SetAssetBundleNameAndVariant("", "");
                    TagSource(importer, assetPath, dumpRoot);
                }

                var importedClipCount = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                    .OfType<AnimationClip>()
                    .Count(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal));
                if (importedClipCount == 0)
                {
                    throw new InvalidOperationException("FBX imported without animation clips: " + assetPath);
                }
                if (importedClipCount < clips.Length)
                {
                    log.Add("FBX WARNING: " + assetPath + " imported " + importedClipCount + " clips from " + clips.Length + " controller clips; review the animation takes.");
                }

                log.Add("FBX: " + modelPath + " + " + clips.Length + " controller clips -> " + assetPath);
            }
            finally
            {
                if (temporaryController) { UnityEngine.Object.DestroyImmediate(temporaryController); }
                PrefabUtility.UnloadPrefabContents(instance);
            }
        }

        private static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) { return; }
            var parent = assetPath.Substring(0, assetPath.LastIndexOf('/'));
            AssetDatabase.CreateFolder(parent, assetPath.Substring(parent.Length + 1));
        }

        private static void TagSource(AssetImporter importer, string fbxPath, string dumpRoot)
        {
            var guid = AssetDatabase.AssetPathToGUID(dumpRoot);
            if (string.IsNullOrEmpty(guid)) { throw new InvalidOperationException("Source dump has no folder GUID: " + dumpRoot); }
            const string prefix = "bushtail.source-dump-guid=";
            var lines = importer.userData.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            lines.Add(prefix + guid);
            importer.userData = string.Join("\n", lines);
            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(fbxPath);
        }
    }
}
