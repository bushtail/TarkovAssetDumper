#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Editor.bushtail
{
    /// <summary>Copies compatible FBX edits into the original assets without changing their GUIDs.</summary>
    internal static class AssetBundleWeaponFbxApply
    {
        private sealed class MeshEdit
        {
            internal Mesh Source = null!;
            internal Mesh Target = null!;
        }

        private sealed class ClipEdit
        {
            internal AnimationClip Source = null!;
            internal AnimationClip Target = null!;
            internal EditorCurveBinding[] SourceBindings = Array.Empty<EditorCurveBinding>();
        }

        [MenuItem("Assets/bushtail/Apply Edited Weapon FBX and Build", false, 2004)]
        private static void ApplySelected()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            var log = new List<string>();
            var built = Apply(path, log);
            Debug.Log(string.Join("\n", log) + "\nBUILD: " + built);
            EditorUtility.RevealInFinder(built);
        }

        [MenuItem("Assets/bushtail/Apply Edited Weapon FBX and Build", true)]
        private static bool CanApplySelected()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return path.StartsWith(AssetBundleWeaponFbx.OutputRoot + "/", StringComparison.Ordinal)
                && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        internal static string Apply(string fbxPath, List<string> log, bool build = true)
        {
            if (!CanApplyPath(fbxPath)) { throw new ArgumentException("Select a weapon FBX in " + AssetBundleWeaponFbx.OutputRoot, nameof(fbxPath)); }
            var dumpName = fbxPath.Substring(AssetBundleWeaponFbx.OutputRoot.Length + 1).Split('/')[0];
            var importer = AssetImporter.GetAtPath(fbxPath);
            const string prefix = "bushtail.source-dump-guid=";
            var guid = importer?.userData.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .SingleOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
            var dumpRoot = string.IsNullOrEmpty(guid) ? "" : AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(guid) && string.IsNullOrEmpty(dumpRoot))
            {
                throw new DirectoryNotFoundException("The original dump referenced by this FBX was removed (folder GUID " + guid + ").");
            }
            if (string.IsNullOrEmpty(dumpRoot))
            {
                var roots = AssetDatabase.FindAssets("l:BundleDumperExport")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(path => AssetDatabase.IsValidFolder(path)
                        && Path.GetFileName(path).Equals(dumpName, StringComparison.Ordinal))
                    .ToArray();
                var preferred = "Assets/BundleDumps/" + dumpName;
                if (roots.Contains(preferred, StringComparer.Ordinal)) { dumpRoot = preferred; }
                else if (roots.Length == 1) { dumpRoot = roots[0]; }
                else { throw new DirectoryNotFoundException("Expected one labeled original dump folder named " + dumpName + "; found " + roots.Length); }
            }
            if (!AssetDatabase.IsValidFolder(dumpRoot) || !Path.GetFileName(dumpRoot).Equals(dumpName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("FBX source folder GUID does not match a current dump: " + dumpRoot);
            }

            if (importer is ModelImporter modelImporter && !modelImporter.isReadable)
            {
                modelImporter.isReadable = true;
                modelImporter.SaveAndReimport();
            }

            var edited = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (!edited) { throw new InvalidDataException("The edited FBX did not import as a model: " + fbxPath); }
            var modelPaths = AssetDatabase.FindAssets("t:Prefab", new[] { dumpRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => Path.GetFileNameWithoutExtension(path).Equals(edited.name, StringComparison.Ordinal))
                .ToArray();
            if (modelPaths.Length != 1) { throw new InvalidDataException("Expected one original weapon model named " + edited.name + "; found " + modelPaths.Length); }
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPaths[0]);
            if (!model) { throw new InvalidDataException("Could not load original model: " + modelPaths[0]); }

            var modelTransformPaths = new HashSet<string>(model.GetComponentsInChildren<Transform>(true)
                .Select(transform => RelativePath(model.transform, transform)), StringComparer.Ordinal);
            var meshEdits = MatchMeshes(edited, model, dumpRoot, log);
            var clipEdits = MatchClips(fbxPath, dumpRoot, modelTransformPaths, log);
            if (meshEdits.Count == 0 && clipEdits.Count == 0)
            {
                throw new InvalidDataException("No compatible mesh or animation edits were found. Review the skipped paths in the Console.");
            }

            var targets = meshEdits.Select(edit => AssetDatabase.GetAssetPath(edit.Target))
                .Concat(clipEdits.Select(edit => AssetDatabase.GetAssetPath(edit.Target)))
                .Distinct(StringComparer.Ordinal).ToArray();
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var backup = Path.Combine(project, "Library", "BundleDumper", "EditedFbxBackups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            foreach (var path in targets)
            {
                var absolute = Path.Combine(project, path.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(backup, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(absolute, destination);
                if (File.Exists(absolute + ".meta")) { File.Copy(absolute + ".meta", destination + ".meta"); }
            }
            log.Add("EDIT BACKUP: " + backup);

            foreach (var edit in meshEdits)
            {
                Undo.RegisterCompleteObjectUndo(edit.Target, "Apply edited weapon mesh");
                CopyMesh(edit.Source, edit.Target);
                EditorUtility.SetDirty(edit.Target);
            }
            foreach (var edit in clipEdits)
            {
                Undo.RegisterCompleteObjectUndo(edit.Target, "Apply edited weapon animation");
                foreach (var binding in AnimationUtility.GetCurveBindings(edit.Target).Where(binding => binding.type == typeof(Transform)))
                {
                    AnimationUtility.SetEditorCurve(edit.Target, binding, null);
                }
                foreach (var binding in edit.SourceBindings)
                {
                    AnimationUtility.SetEditorCurve(edit.Target, binding, AnimationUtility.GetEditorCurve(edit.Source, binding));
                }
                edit.Target.frameRate = edit.Source.frameRate;
                EditorUtility.SetDirty(edit.Target);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            log.Add("EDIT APPLIED: " + meshEdits.Count + " meshes and " + clipEdits.Count + " clips; existing GUIDs, controller links, game components and animation events retained.");
            return build ? AssetBundleDumpBuilder.Build(dumpRoot) : dumpRoot;
        }

        private static bool CanApplyPath(string path) => !string.IsNullOrEmpty(path)
            && path.StartsWith(AssetBundleWeaponFbx.OutputRoot + "/", StringComparison.Ordinal)
            && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
            && path.Substring(AssetBundleWeaponFbx.OutputRoot.Length + 1).Count(c => c == '/') == 1;

        private static List<MeshEdit> MatchMeshes(GameObject edited, GameObject model, string dumpRoot, List<string> log)
        {
            var source = edited.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => SharedMesh(renderer))
                .GroupBy(renderer => RelativePath(edited.transform, renderer.transform), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var target = model.GetComponentsInChildren<Renderer>(true).Where(renderer => SharedMesh(renderer)).ToArray();
            var result = new List<MeshEdit>();
            var used = new HashSet<Mesh>();
            foreach (var original in target)
            {
                var path = RelativePath(model.transform, original.transform);
                if (!source.TryGetValue(path, out var matches) || matches.Length != 1 || matches[0].GetType() != original.GetType())
                {
                    log.Add("MESH SKIPPED: no unique same-type FBX renderer at " + path);
                    continue;
                }
                var oldMesh = SharedMesh(original)!;
                var newMesh = SharedMesh(matches[0])!;
                if (!newMesh.isReadable) { throw new InvalidDataException("Edited FBX mesh is not readable: " + newMesh.name); }
                var oldPath = AssetDatabase.GetAssetPath(oldMesh);
                if (!oldPath.StartsWith(dumpRoot + "/", StringComparison.Ordinal)
                    || !oldPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    log.Add("MESH SKIPPED: original mesh is not a standalone dump asset: " + oldPath);
                    continue;
                }
                if (original is SkinnedMeshRenderer oldSkin && matches[0] is SkinnedMeshRenderer newSkin)
                {
                    var oldBones = oldSkin.bones.Select(bone => bone ? bone.name : "").ToArray();
                    var newBones = newSkin.bones.Select(bone => bone ? bone.name : "").ToArray();
                    if (!oldBones.SequenceEqual(newBones, StringComparer.Ordinal)
                        || newMesh.bindposes.Length != oldBones.Length)
                    {
                        log.Add("MESH SKIPPED: skin bone order differs at " + path);
                        continue;
                    }
                }
                if (used.Add(oldMesh)) { result.Add(new MeshEdit { Source = newMesh, Target = oldMesh }); }
            }
            return result;
        }

        private static List<ClipEdit> MatchClips(string fbxPath, string dumpRoot, HashSet<string> modelPaths, List<string> log)
        {
            var source = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<AnimationClip>()
                .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
                .GroupBy(clip => clip.name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var target = AssetDatabase.FindAssets("t:AnimationClip", new[] { dumpRoot })
                .Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                .Select(AssetDatabase.LoadAssetAtPath<AnimationClip>).Where(clip => clip)
                .GroupBy(clip => clip.name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var result = new List<ClipEdit>();
            foreach (var pair in source)
            {
                if (pair.Value.Length != 1 || !target.TryGetValue(pair.Key, out var matches) || matches.Length != 1)
                {
                    log.Add("CLIP SKIPPED: no unique original clip named " + pair.Key);
                    continue;
                }
                var bindings = AnimationUtility.GetCurveBindings(pair.Value[0])
                    .Where(binding => binding.type == typeof(Transform)).ToArray();
                if (bindings.Length == 0 || bindings.Any(binding => !modelPaths.Contains(binding.path)))
                {
                    log.Add("CLIP SKIPPED: transform curves do not fit the original skeleton: " + pair.Key);
                    continue;
                }
                result.Add(new ClipEdit { Source = pair.Value[0], Target = matches[0], SourceBindings = bindings });
            }
            var untouched = target.Values.SelectMany(matches => matches)
                .Count(clip => result.All(edit => edit.Target != clip));
            if (untouched > 0) { log.Add("CLIP RETAINED: " + untouched + " original clips were not mapped from this FBX."); }
            return result;
        }

        private static Mesh? SharedMesh(Renderer renderer) => renderer switch
        {
            SkinnedMeshRenderer skin => skin.sharedMesh,
            MeshRenderer => renderer.GetComponent<MeshFilter>()?.sharedMesh,
            _ => null
        };

        private static void CopyMesh(Mesh source, Mesh target)
        {
            target.Clear(false);
            target.indexFormat = source.indexFormat;
            target.vertices = source.vertices;
            target.normals = source.normals;
            target.tangents = source.tangents;
            target.colors = source.colors;
            for (var channel = 0; channel < 8; channel++)
            {
                var uv = new List<Vector4>();
                source.GetUVs(channel, uv);
                if (uv.Count > 0) { target.SetUVs(channel, uv); }
            }
            target.bindposes = source.bindposes;
            target.SetBoneWeights(source.GetBonesPerVertex(), source.GetAllBoneWeights());
            target.subMeshCount = source.subMeshCount;
            for (var submesh = 0; submesh < source.subMeshCount; submesh++)
            {
                target.SetIndices(source.GetIndices(submesh, false), source.GetTopology(submesh), submesh,
                    false, checked((int)source.GetBaseVertex(submesh)));
            }
            for (var shape = 0; shape < source.blendShapeCount; shape++)
            {
                var name = source.GetBlendShapeName(shape);
                for (var frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
                {
                    var vertices = new Vector3[source.vertexCount];
                    var normals = new Vector3[source.vertexCount];
                    var tangents = new Vector3[source.vertexCount];
                    source.GetBlendShapeFrameVertices(shape, frame, vertices, normals, tangents);
                    target.AddBlendShapeFrame(name, source.GetBlendShapeFrameWeight(shape, frame), vertices, normals, tangents);
                }
            }
            target.bounds = source.bounds;
        }

        private static string RelativePath(Transform root, Transform current)
        {
            if (current == root) { return ""; }
            var names = new Stack<string>();
            while (current && current != root) { names.Push(current.name); current = current.parent; }
            return current == root ? string.Join("/", names) : "";
        }
    }
}
